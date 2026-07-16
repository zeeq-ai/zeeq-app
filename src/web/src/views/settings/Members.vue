<template>
  <div>
    <UPageCard
      title="Members"
      description="Invite people and manage active organization roles."
      variant="naked"
      orientation="horizontal"
      class="mb-4"
    >
      <USelect
        v-model="statusFilter"
        :items="statusFilterItems"
        color="neutral"
        class="w-36 lg:ms-auto"
      />
    </UPageCard>

    <UPageCard
      variant="subtle"
      :ui="{
        container: 'p-0 sm:p-0 gap-y-0',
        wrapper: 'items-stretch',
        header: 'p-4 mb-0 border-b border-default',
      }"
    >
      <template #header>
        <UInput
          v-model="query"
          icon="i-hugeicons-search-01"
          placeholder="Search members"
          class="w-full"
        />
      </template>

      <MembersList
        :members="filteredMembers"
        :invitations="filteredOrganizationInvitations"
        :can-manage="canManageOrganization"
        :saving
        :current-user-id="currentUserId"
        @role-change="changeRole"
        @remove="removeMember"
        @cancel-invitation="cancelInvitation"
      />

      <!-- Invite row belongs below the member list and shares the list padding. -->
      <div
        class="flex items-center justify-between gap-3 border-t border-default px-4 py-3 sm:px-6"
      >
        <div class="flex min-w-0 flex-1 items-center gap-3">
          <UAvatar icon="i-hugeicons-user-add-02" size="md" />
          <UInput
            v-model="inviteEmail"
            icon="i-hugeicons-mail-01"
            placeholder="person@example.com"
            :disabled="!canManageOrganization || saving"
            class="min-w-0 flex-1"
          />
        </div>

        <div class="flex shrink-0 items-center gap-2">
          <USelect
            v-model="inviteRole"
            :items="roleItems"
            color="neutral"
            :disabled="!canManageOrganization || saving"
            class="w-32"
          />
          <UTooltip text="Invite member">
            <UButton
              aria-label="Invite member"
              icon="i-hugeicons-mail-send-01"
              color="neutral"
              variant="ghost"
              square
              :loading="saving"
              :disabled="!canManageOrganization || !inviteEmail"
              @click="inviteMember"
            />
          </UTooltip>
        </div>
      </div>
    </UPageCard>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { storeToRefs } from "pinia";
import MembersList from "./components/MembersList.vue";
import {
  organizationRoleOptions,
  useOrganizationSettingsStore,
} from "@/stores/organization-settings-store";
import { useAppStore } from "@/stores/app-store";

const toast = useToast();
const appStore = useAppStore();
const settingsStore = useOrganizationSettingsStore();
const { user: me } = storeToRefs(appStore);
const { members, organizationInvitations, saving, canManageOrganization } =
  storeToRefs(settingsStore);

const query = ref("");
const statusFilter = ref<"all" | "active" | "pending">("all");
const inviteEmail = ref("");
const inviteRole = ref<"owner" | "admin" | "member">("member");

const currentUserId = computed(() => me.value?.userId ?? null);
const statusFilterItems = [
  { label: "All", value: "all" },
  { label: "Active", value: "active" },
  { label: "Pending", value: "pending" },
] satisfies Array<{ label: string; value: "all" | "active" | "pending" }>;
const roleItems = organizationRoleOptions.map((role) => ({
  label: role,
  value: role,
}));

/**
 * Search is intentionally local because member lists are already scoped to the
 * active organization and expected to stay small for this phase.
 */
const filteredMembers = computed(() => {
  if (statusFilter.value === "pending") {
    return [];
  }

  const normalizedQuery = query.value.trim().toLowerCase();
  if (!normalizedQuery) {
    return members.value;
  }

  return members.value.filter((member) => {
    const values = [member.displayName, member.email ?? "", member.role].map(
      (value) => value.toLowerCase(),
    );

    return values.some((value) => value.includes(normalizedQuery));
  });
});

const filteredOrganizationInvitations = computed(() => {
  if (statusFilter.value === "active") {
    return [];
  }

  const normalizedQuery = query.value.trim().toLowerCase();
  if (!normalizedQuery) {
    return organizationInvitations.value;
  }

  return organizationInvitations.value.filter((invitation) => {
    const values = [
      invitation.invitedEmail ?? "",
      invitation.role,
      invitation.organizationName ?? "",
    ].map((value) => value.toLowerCase());

    return values.some((value) => value.includes(normalizedQuery));
  });
});

/** Loads members and sent invitations when the nested settings route opens directly. */
onMounted(async () => {
  try {
    await Promise.all([
      settingsStore.loadMembers(),
      settingsStore.loadOrganizationInvitations(),
    ]);
  } catch (err: unknown) {
    showError(err instanceof Error ? err.message : "Could not load members.");
  }
});

/** Sends an invitation for the active organization. */
async function inviteMember() {
  try {
    await settingsStore.createInvitation({
      email: inviteEmail.value.trim(),
      role: inviteRole.value,
    });
    inviteEmail.value = "";
    inviteRole.value = "member";
    toast.add({
      title: "Invitation sent",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not send invitation.",
    );
  }
}

/** Cancels a sent invitation that has not been accepted yet. */
async function cancelInvitation(invitationId: string) {
  try {
    await settingsStore.cancelOrganizationInvitation(invitationId);
    toast.add({
      title: "Invitation canceled",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(
      err instanceof Error ? err.message : "Could not cancel invitation.",
    );
  }
}

/** Applies a member role update through the settings store. */
async function changeRole(userId: string, role: "owner" | "admin" | "member") {
  try {
    await settingsStore.changeMemberRole(userId, role);
    toast.add({
      title: "Role updated",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(err instanceof Error ? err.message : "Could not update role.");
  }
}

/** Removes a member from the active organization. */
async function removeMember(userId: string) {
  try {
    await settingsStore.removeMember(userId);
    toast.add({
      title: "Member removed",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError(err instanceof Error ? err.message : "Could not remove member.");
  }
}

/** Shows membership API errors in the shared toast surface. */
function showError(message: string) {
  toast.add({
    title: "Member update failed",
    description: message,
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
