import { defineStore, acceptHMRUpdate, storeToRefs } from "pinia";
import { Invitations, Memberships, Organizations } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import type { ChangeMemberRoleRequest } from "@/api/generated/types/ChangeMemberRoleRequest";
import type { CreateInvitationRequest } from "@/api/generated/types/CreateInvitationRequest";
import type { CreateOrganizationRequest } from "@/api/generated/types/CreateOrganizationRequest";
import type { InvitationResponse } from "@/api/generated/types/InvitationResponse";
import type { MemberResponse } from "@/api/generated/types/MemberResponse";
import type { OrganizationResponse } from "@/api/generated/types/OrganizationResponse";
import type { SameDomainOnboardingStatusResponse } from "@/api/generated/types/SameDomainOnboardingStatusResponse";
import type { UpdateOrganizationRequest } from "@/api/generated/types/UpdateOrganizationRequest";
import type { UpdateSameDomainOnboardingRequest } from "@/api/generated/types/UpdateSameDomainOnboardingRequest";

/**
 * Canonical role values accepted by the membership API and used by settings UI.
 */
export const organizationRoleOptions = ["owner", "admin", "member"] as const;
export const sameDomainOnboardingRoleOptions = ["member", "admin"] as const;

type OrganizationRole = (typeof organizationRoleOptions)[number];
export type SameDomainOnboardingRole =
  (typeof sameDomainOnboardingRoleOptions)[number];

/**
 * Store for organization settings screens.
 *
 * The generated Kubb clients are configured for same-origin `/api/v1` calls so
 * HttpOnly cookie auth follows the same path as the rest of the Vue app.
 */
export const useOrganizationSettingsStore = defineStore(
  "organization-settings-store",
  () => {
    const appStore = useAppStore();
    const { user: me } = storeToRefs(appStore);

    const organization = ref<OrganizationResponse | null>(null);
    const members = ref<MemberResponse[]>([]);
    const invitations = ref<InvitationResponse[]>([]);
    const organizationInvitations = ref<InvitationResponse[]>([]);
    const loading = ref(false);
    const saving = ref(false);
    const error = ref<string | null>(null);

    const currentOrganizationId = computed(
      () => me.value?.organizationId ?? null,
    );
    const currentUserRole = computed(() => me.value?.organizationRole ?? null);
    const canManageOrganization = computed(
      () =>
        currentUserRole.value === "owner" || currentUserRole.value === "admin",
    );

    /**
     * Loads org details for the active organization from /me.
     */
    async function loadCurrentOrganization() {
      if (!currentOrganizationId.value) {
        organization.value = null;
        return;
      }

      organization.value = await Organizations.getOrganization(
        currentOrganizationId.value,
      );
    }

    /**
     * Loads active members for the current organization.
     */
    async function loadMembers() {
      if (!currentOrganizationId.value) {
        members.value = [];
        return;
      }

      members.value = await Organizations.listMembers(
        currentOrganizationId.value,
      );
    }

    /**
     * Loads pending invitations sent for the current organization.
     */
    async function loadOrganizationInvitations() {
      if (!currentOrganizationId.value || !canManageOrganization.value) {
        organizationInvitations.value = [];
        return;
      }

      organizationInvitations.value =
        await Invitations.listOrganizationInvitations(
          currentOrganizationId.value,
        );
    }

    /**
     * Loads pending invitations addressed to the signed-in user's email.
     */
    async function loadInvitations() {
      invitations.value = await Invitations.listInvitations();
    }

    /**
     * Refreshes the settings slice for the active organization.
     */
    async function loadSettings() {
      loading.value = true;
      error.value = null;

      try {
        await Promise.all([
          loadCurrentOrganization(),
          loadMembers(),
          loadOrganizationInvitations(),
          loadInvitations(),
        ]);
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        loading.value = false;
      }
    }

    /**
     * Updates display metadata for the current organization and refreshes /me
     * because display metadata can be reflected in shell state.
     */
    async function updateOrganization(request: UpdateOrganizationRequest) {
      if (!currentOrganizationId.value) {
        return;
      }

      saving.value = true;
      error.value = null;

      try {
        organization.value = await Organizations.updateOrganization(
          currentOrganizationId.value,
          request,
        );
        await appStore.fetchUser({ force: true });
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Updates same-domain onboarding settings for the active organization.
     */
    async function updateSameDomainOnboarding(request: {
      enabled: boolean;
      defaultRole?: SameDomainOnboardingRole | null;
    }) {
      const organizationId = currentOrganizationId.value;
      const organizationAtStart = organization.value;
      if (!organizationId || !organizationAtStart) {
        return;
      }

      const updateRequest: UpdateSameDomainOnboardingRequest = {
        enabled: request.enabled,
        defaultRole: request.defaultRole,
      };

      saving.value = true;
      error.value = null;

      try {
        const status = await Organizations.updateSameDomainOnboarding(
          organizationId,
          updateRequest,
        );
        await appStore.fetchUser({ force: true });

        if (
          currentOrganizationId.value === organizationId &&
          organization.value === organizationAtStart
        ) {
          patchSameDomainOnboardingStatus(organizationAtStart, status);
        }

        return status;
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Creates an organization, switches the active org to it, and refreshes
     * settings state for the new context.
     */
    async function createOrganization(request: CreateOrganizationRequest) {
      saving.value = true;
      error.value = null;

      try {
        const created = await Organizations.createOrganization(request);
        await appStore.switchOrganization(created.id);
        await loadSettings();

        return created;
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Sends a pending organization invitation.
     */
    async function createInvitation(request: CreateInvitationRequest) {
      if (!currentOrganizationId.value) {
        return;
      }

      saving.value = true;
      error.value = null;

      try {
        await Invitations.createInvitation(
          currentOrganizationId.value,
          request,
        );
        await loadOrganizationInvitations();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Changes a member role in the current organization.
     */
    async function changeMemberRole(userId: string, role: OrganizationRole) {
      if (!currentOrganizationId.value) {
        return;
      }

      const request: ChangeMemberRoleRequest = { role };

      saving.value = true;
      error.value = null;

      try {
        await Organizations.changeMemberRole(
          currentOrganizationId.value,
          userId,
          request,
        );
        await loadMembers();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Removes a member from the current organization.
     */
    async function removeMember(userId: string) {
      if (!currentOrganizationId.value) {
        return;
      }

      saving.value = true;
      error.value = null;

      try {
        await Organizations.removeMember(currentOrganizationId.value, userId);
        await loadMembers();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Leaves an organization from the user's own memberships list.
     */
    async function leaveOrganization(orgId: string) {
      saving.value = true;
      error.value = null;

      try {
        const wasCurrentOrganization = orgId === currentOrganizationId.value;

        await Memberships.leaveOrganization(orgId);
        await appStore.fetchUser({ force: true });

        if (!wasCurrentOrganization && currentOrganizationId.value) {
          await loadSettings();
          return;
        }

        organization.value = null;
        members.value = [];
        organizationInvitations.value = [];
        await loadInvitations();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Sets a default organization for future sign-ins.
     */
    async function setDefaultOrganization(orgId: string) {
      saving.value = true;
      error.value = null;

      try {
        await Memberships.setDefaultOrganization(orgId);
        await appStore.fetchUser();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Accepts a pending invitation and refreshes memberships.
     */
    async function acceptInvitation(
      invitationId: string,
      organizationId?: string,
    ) {
      saving.value = true;
      error.value = null;

      try {
        await appStore.acceptInvitation(invitationId, organizationId);
        await loadInvitations();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Declines a pending invitation and removes it from the user's queue.
     */
    async function declineInvitation(invitationId: string) {
      saving.value = true;
      error.value = null;

      try {
        await Invitations.declineInvitation(invitationId);
        await loadInvitations();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    /**
     * Cancels an organization-scoped invitation that has not been accepted yet.
     */
    async function cancelOrganizationInvitation(invitationId: string) {
      if (!currentOrganizationId.value) {
        return;
      }

      saving.value = true;
      error.value = null;

      try {
        await Invitations.cancelInvitation(
          currentOrganizationId.value,
          invitationId,
        );
        await loadOrganizationInvitations();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        saving.value = false;
      }
    }

    return {
      organization,
      members,
      invitations,
      organizationInvitations,
      loading,
      saving,
      error,
      currentOrganizationId,
      currentUserRole,
      canManageOrganization,
      loadSettings,
      loadCurrentOrganization,
      loadMembers,
      loadOrganizationInvitations,
      loadInvitations,
      updateOrganization,
      updateSameDomainOnboarding,
      createOrganization,
      createInvitation,
      changeMemberRole,
      removeMember,
      leaveOrganization,
      setDefaultOrganization,
      acceptInvitation,
      declineInvitation,
      cancelOrganizationInvitation,
    };
  },
);

/** Normalizes thrown values for UI error surfaces. */
function toErrorMessage(err: unknown): string {
  return err instanceof Error ? err.message : "Unknown settings error";
}

function patchSameDomainOnboardingStatus(
  organization: OrganizationResponse,
  status: SameDomainOnboardingStatusResponse,
) {
  organization.autoInviteSameDomainEnabled = status.enabled;
  organization.autoInviteSameDomain = status.domain;
  organization.autoInviteDefaultRole = status.defaultRole;
  organization.autoInviteSameDomainCanEnable = status.canEnable;
  organization.autoInviteSameDomainBlockReason = status.blockReason;
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useOrganizationSettingsStore, import.meta.hot),
  );
}
