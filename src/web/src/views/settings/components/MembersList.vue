<template>
  <!-- Active organization members with inline role management. -->
  <ul role="list" class="divide-y divide-default">
    <li
      v-for="member in members"
      :key="member.userId"
      class="flex items-center justify-between gap-3 px-4 py-3 sm:px-6"
    >
      <div class="flex min-w-0 items-center gap-3">
        <UAvatar
          :src="member.pictureUrl || undefined"
          :alt="member.displayName"
          size="md"
        />

        <div class="min-w-0 text-sm">
          <p class="truncate font-medium text-highlighted">
            {{ member.displayName }}
          </p>
          <p class="truncate text-muted">
            {{ member.email || member.userId }}
          </p>
        </div>
      </div>

      <div class="flex shrink-0 items-center gap-2">
        <USelect
          :model-value="selectedRole(member.role)"
          :items="roleItems"
          color="neutral"
          :disabled="!canManage || saving"
          class="w-32"
          @update:model-value="updateRole(member.userId, member.role, $event)"
        />

        <ZeeqPopConfirm
          title="Remove Member"
          :body="`Remove ${member.displayName} from this organization?`"
          confirm-label="Remove"
          icon="i-hugeicons-delete-02"
          color="error"
          variant="ghost"
          :disabled="!canManage || saving || member.userId === currentUserId"
          @confirm="emits('remove', member.userId)"
        />
      </div>
    </li>

    <li
      v-for="invitation in invitations"
      :key="invitation.id"
      class="flex items-center justify-between gap-3 px-4 py-3 sm:px-6"
    >
      <div class="flex min-w-0 items-center gap-3">
        <UAvatar icon="i-hugeicons-mail-account-01" size="md" />

        <div class="min-w-0 text-sm">
          <p class="truncate font-medium text-highlighted">
            {{ invitation.invitedEmail || invitation.id }}
          </p>
          <p class="truncate text-muted">Invited as {{ invitation.role }}</p>
        </div>
      </div>

      <div class="flex shrink-0 items-center gap-2">
        <UBadge color="neutral" variant="subtle" class="rounded-full">
          Pending
        </UBadge>

        <ZeeqPopConfirm
          title="Cancel Invitation"
          :body="`Cancel the invitation for ${invitation.invitedEmail || 'this user'}?`"
          confirm-label="Delete"
          icon="i-hugeicons-delete-02"
          color="error"
          variant="ghost"
          :disabled="!canManage || saving"
          @confirm="emits('cancelInvitation', invitation.id)"
        />
      </div>
    </li>
  </ul>
</template>

<script setup lang="ts">
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import type { InvitationResponse } from "@/api/generated/types/InvitationResponse";
import type { MemberResponse } from "@/api/generated/types/MemberResponse";
import { organizationRoleOptions } from "@/stores/organization-settings-store";

defineProps<{
  members: MemberResponse[];
  invitations: InvitationResponse[];
  canManage: boolean;
  saving: boolean;
  currentUserId: string | null;
}>();

const emits = defineEmits<{
  roleChange: [userId: string, role: "owner" | "admin" | "member"];
  remove: [userId: string];
  cancelInvitation: [invitationId: string];
}>();

/**
 * USelect options mirror the backend membership role strings.
 */
const roleItems = organizationRoleOptions.map((role) => ({
  label: role,
  value: role,
}));

/**
 * Emits a role change only when USelect yields one of the known role values.
 */
function updateRole(userId: string, previousRole: string, nextRole: unknown) {
  if (typeof nextRole !== "string" || nextRole === previousRole) {
    return;
  }

  if (!isOrganizationRole(nextRole)) {
    return;
  }

  emits("roleChange", userId, nextRole);
}

/** Narrows arbitrary USelect values to valid organization roles. */
function isOrganizationRole(
  role: string,
): role is "owner" | "admin" | "member" {
  return organizationRoleOptions.some((option) => option === role);
}

/** Converts backend strings to a role literal accepted by USelect. */
function selectedRole(role: string): "owner" | "admin" | "member" | undefined {
  return isOrganizationRole(role) ? role : undefined;
}
</script>
