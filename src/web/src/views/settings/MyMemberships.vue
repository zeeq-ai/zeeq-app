<template>
  <div class="grid gap-6">
    <section>
      <UPageCard
        title="My Memberships"
        description="Switch active organizations, choose a default, or leave organizations."
        variant="naked"
        class="mb-4"
      />

      <UPageCard variant="subtle" :ui="{ container: 'p-0 sm:p-0 gap-y-0' }">
        <!-- Active memberships returned by /me drive tenancy actions. -->
        <ul role="list" class="divide-y divide-default">
          <li
            v-for="org in activeOrganizations"
            :key="org.id"
            class="flex items-center justify-between gap-3 px-4 py-3 sm:px-6"
          >
            <div class="flex min-w-0 items-center gap-3">
              <UAvatar
                :src="org.iconUrl || undefined"
                icon="i-hugeicons-cube"
                size="md"
              />
              <div class="min-w-0 text-sm">
                <p class="truncate font-medium text-highlighted">
                  {{ org.displayName }}
                </p>
                <p class="truncate text-muted">
                  {{ org.role }}
                  <span v-if="org.isDefault"> &middot; default</span>
                  <span v-if="org.id === currentOrganizationId">
                    &middot; active
                  </span>
                </p>
              </div>
            </div>

            <div class="flex shrink-0 flex-wrap justify-end gap-2">
              <UButton
                label="Switch"
                icon="i-hugeicons-arrow-reload-horizontal"
                size="xs"
                color="neutral"
                variant="ghost"
                :disabled="saving || org.id === currentOrganizationId"
                @click="switchOrganization(org.id)"
              />
              <UButton
                label="Default"
                icon="i-hugeicons-star"
                size="xs"
                color="neutral"
                variant="ghost"
                :disabled="saving || org.isDefault"
                @click="setDefault(org.id)"
              />
              <ZeeqPopConfirm
                title="Leave Organization"
                :body="`Leave ${org.displayName}? You will lose access unless you are invited again.`"
                confirm-label="Leave"
                icon="i-hugeicons-delete-02"
                size="xs"
                color="error"
                variant="ghost"
                :disabled="saving"
                @confirm="leaveOrganization(org.id)"
              />
            </div>
          </li>
        </ul>
      </UPageCard>
    </section>

    <section>
      <UPageCard
        title="Pending Invitations"
        description="Invitations addressed to your signed-in email."
        variant="naked"
        orientation="horizontal"
        class="mb-4"
      />

      <UPageCard
        v-if="invitations.length > 0"
        variant="subtle"
        :ui="{ container: 'p-0 sm:p-0 gap-y-0' }"
      >
        <ul role="list" class="divide-y divide-default">
          <InvitationRow
            v-for="invitation in invitations"
            :key="invitation.id"
            :invitation
            :saving
            @accept="acceptInvitation"
            @decline="declineInvitation"
          />
        </ul>
      </UPageCard>

      <UAlert
        v-else
        icon="i-hugeicons-information-circle"
        color="neutral"
        variant="subtle"
        title="No pending invitations"
      />
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted } from "vue";
import { storeToRefs } from "pinia";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import InvitationRow from "./components/InvitationRow.vue";
import { useAppStore } from "@/stores/app-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";

const toast = useToast();
const appStore = useAppStore();
const settingsStore = useOrganizationSettingsStore();
const { user: me } = storeToRefs(appStore);
const { invitations, saving } = storeToRefs(settingsStore);

const currentOrganizationId = computed(() => me.value?.organizationId ?? null);

/**
 * Active org rows come from /me because this view is about the caller's own
 * tenancy choices rather than organization administration.
 */
const activeOrganizations = computed(
  () => me.value?.organizations?.filter((org) => org.status === "Active") ?? [],
);

/** Loads pending invitations when the view is opened directly. */
onMounted(async () => {
  try {
    await settingsStore.loadInvitations();
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not load invitations.",
    );
  }
});

/** Switches the active org and refreshes settings state. */
async function switchOrganization(orgId: string) {
  try {
    await appStore.switchOrganization(orgId);
    await settingsStore.loadSettings();
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not switch organization.",
    );
  }
}

/** Marks an organization as the default for future sessions. */
async function setDefault(orgId: string) {
  try {
    await settingsStore.setDefaultOrganization(orgId);
    toast.add({
      title: "Default organization updated",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(err instanceof Error ? err.message : "Could not set default.");
  }
}

/** Leaves a membership from the caller-owned memberships list. */
async function leaveOrganization(orgId: string) {
  try {
    await settingsStore.leaveOrganization(orgId);
    toast.add({
      title: "Organization left",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not leave organization.",
    );
  }
}

/** Accepts an invitation, then refreshes the caller's organization list. */
async function acceptInvitation(invitationId: string, organizationId: string) {
  try {
    await settingsStore.acceptInvitation(invitationId, organizationId);
    toast.add({
      title: "Invitation accepted",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not accept invitation.",
    );
  }
}

/** Declines an invitation and removes it from the pending list. */
async function declineInvitation(invitationId: string) {
  try {
    await settingsStore.declineInvitation(invitationId);
    toast.add({
      title: "Invitation declined",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not decline invitation.",
    );
  }
}

/** Shows membership API errors in the shared toast surface. */
function showError(message: string) {
  toast.add({
    title: "Membership update failed",
    description: message,
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
