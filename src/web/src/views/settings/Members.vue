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
        header: 'p-0 mb-0 border-b border-default',
      }"
    >
      <template #header>
        <!-- Invite row sits above search so adding members is the primary action. -->
        <div
          class="flex flex-col gap-3 px-4 py-3 sm:flex-row sm:items-center sm:justify-between sm:px-6"
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
      </template>

      <div class="border-b border-default px-4 py-3 sm:px-6">
        <UInput
          v-model="query"
          icon="i-hugeicons-search-01"
          placeholder="Search members"
          class="w-full"
        />
      </div>

      <MembersList
        :members="pagedMembers"
        :invitations="pagedOrganizationInvitations"
        :can-manage="canManageOrganization"
        :saving
        :current-user-id="currentUserId"
        @role-change="changeRole"
        @remove="removeMember"
        @cancel-invitation="cancelInvitation"
      />

      <!-- Pagination applies after search and status filtering across active and invited rows. -->
      <div
        v-if="filteredEntryCount > 0"
        class="flex flex-col gap-3 border-t border-default p-4 sm:flex-row sm:items-center sm:justify-between"
      >
        <p class="text-sm text-muted">{{ pageRangeLabel }}</p>
        <UPagination
          v-if="filteredEntryCount > pageSize"
          v-model:page="page"
          :items-per-page="pageSize"
          :total="filteredEntryCount"
          variant="soft"
          active-variant="soft"
        />
      </div>
    </UPageCard>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { useFuse } from "@vueuse/integrations/useFuse";
import type { InvitationResponse } from "@/api/generated/types/InvitationResponse";
import type { MemberResponse } from "@/api/generated/types/MemberResponse";
import MembersList from "./components/MembersList.vue";
import {
  organizationRoleOptions,
  useOrganizationSettingsStore,
} from "@/stores/organization-settings-store";
import { useAppStore } from "@/stores/app-store";

type MemberListEntry =
  | {
      kind: "member";
      member: MemberResponse;
      displayName: string;
      email: string;
      role: string;
    }
  | {
      kind: "invitation";
      invitation: InvitationResponse;
      displayName: string;
      email: string;
      role: string;
    };

const toast = useToast();
const appStore = useAppStore();
const settingsStore = useOrganizationSettingsStore();
const { user: me } = storeToRefs(appStore);
const { members, organizationInvitations, saving, canManageOrganization } =
  storeToRefs(settingsStore);

const pageSize = 10;
const query = ref("");
const page = ref(1);
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
 * Combines active members and pending invitations so search and paging operate
 * over the same ordered result set the user sees.
 */
const memberListEntries = computed<MemberListEntry[]>(() => {
  const memberEntries: MemberListEntry[] = members.value.map((member) => ({
    kind: "member",
    member,
    displayName: member.displayName,
    email: member.email ?? "",
    role: member.role,
  }));

  const invitationEntries: MemberListEntry[] =
    organizationInvitations.value.map((invitation) => ({
      kind: "invitation",
      invitation,
      displayName: invitation.invitedEmail ?? "",
      email: invitation.invitedEmail ?? "",
      role: invitation.role,
    }));

  if (statusFilter.value === "active") {
    return memberEntries;
  }

  if (statusFilter.value === "pending") {
    return invitationEntries;
  }

  return [...memberEntries, ...invitationEntries];
});

const { results: fuseResults } = useFuse(query, memberListEntries, {
  fuseOptions: {
    keys: ["displayName", "email", "role"],
    threshold: 0.35,
    ignoreLocation: true,
  },
});

/** Rows matching the active status filter and Fuse search query. */
const filteredEntries = computed<MemberListEntry[]>(() => {
  // NOTE: Status filtering intentionally happens upstream in memberListEntries
  // before Fuse receives its collection, so counts, search results, and paging
  // all operate on the same active/pending/all row scope.
  if (!query.value.trim()) {
    return memberListEntries.value;
  }

  return fuseResults.value.map((result) => result.item);
});

const filteredEntryCount = computed(() => filteredEntries.value.length);
const pageCount = computed(() =>
  Math.max(1, Math.ceil(filteredEntryCount.value / pageSize)),
);

/** Filtered rows sliced to the active client-side page. */
const pagedEntries = computed(() => {
  const start = (page.value - 1) * pageSize;

  return filteredEntries.value.slice(start, start + pageSize);
});

const pagedMembers = computed(() =>
  pagedEntries.value
    .filter((entry) => entry.kind === "member")
    .map((entry) => entry.member),
);

const pagedOrganizationInvitations = computed(() =>
  pagedEntries.value
    .filter((entry) => entry.kind === "invitation")
    .map((entry) => entry.invitation),
);

/** Displays the one-based range for the filtered client-side page. */
const pageRangeLabel = computed(() => {
  if (filteredEntryCount.value === 0) {
    return "0 of 0";
  }

  const clampedPage = Math.min(page.value, pageCount.value);
  const start = (clampedPage - 1) * pageSize + 1;
  const end = Math.min(filteredEntryCount.value, clampedPage * pageSize);

  return `${start}-${end} of ${filteredEntryCount.value}`;
});

// Reset to page 1 whenever filters change so stale pages are not shown.
watch([query, statusFilter], () => {
  page.value = 1;
});

// Clamp the active page if filtering or member mutations shrink the result set.
watch(filteredEntryCount, () => {
  if (page.value > pageCount.value) {
    page.value = pageCount.value;
  }
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
