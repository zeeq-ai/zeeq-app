<template>
  <UTooltip text="Pending invitations">
    <UButton
      color="neutral"
      variant="ghost"
      square
      aria-label="Pending invitations"
      :on-click="() => { isOpen = true; }"
    >
      <UChip color="error" inset :show="pendingInvitations.length > 0">
        <UIcon name="i-hugeicons-cube" class="size-5 shrink-0" />
      </UChip>
    </UButton>
  </UTooltip>

  <USlideover v-model:open="isOpen" title="Pending Invitations">
    <template #body>
      <!-- Pending invitation queue sourced from GET /me organization summaries. -->
      <ul
        v-if="pendingInvitations.length > 0"
        role="list"
        class="divide-y divide-default"
      >
        <li
          v-for="invitation in pendingInvitations"
          :key="invitation.invitationId ?? invitation.id"
          class="flex items-center gap-3 px-1 py-4 first:pt-0 last:pb-0"
        >
          <UAvatar
            :src="invitation.iconUrl ?? undefined"
            icon="i-hugeicons-cube"
            size="lg"
            class="shrink-0"
          />

          <div class="flex min-w-0 flex-1 items-center justify-between gap-3">
            <div class="min-w-0">
              <p class="truncate text-sm font-medium text-highlighted">
                {{ invitation.displayName }}
              </p>
              <p class="mt-0.5 text-sm text-muted">
                Invited as {{ invitation.role }}
              </p>
            </div>

            <div class="flex shrink-0 items-center gap-2">
              <UButton
                label="Decline"
                icon="i-hugeicons-cancel-01"
                color="neutral"
                variant="ghost"
                size="sm"
                :loading="
                  pendingAction?.id === invitation.invitationId &&
                  pendingAction.kind === 'decline'
                "
                :disabled="saving || !invitation.invitationId"
                :on-click="() => emitDecline(invitation.invitationId)"
              />
              <UButton
                label="Join"
                icon="i-hugeicons-tick-02"
                color="neutral"
                size="sm"
                :loading="
                  pendingAction?.id === invitation.invitationId &&
                  pendingAction.kind === 'accept'
                "
                :disabled="saving || !invitation.invitationId"
                :on-click="() => emitAccept(invitation.invitationId, invitation.id)"
              />
            </div>
          </div>
        </li>
      </ul>

      <div v-else class="flex min-h-64 flex-col items-center justify-center">
        <UIcon name="i-hugeicons-cube" class="size-14 text-muted" />
        <p class="mt-4 text-sm font-medium text-highlighted">
          No pending invitations
        </p>
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import type { OrgSummary } from "@/api/generated";

type PendingAction = {
  id: string;
  kind: "accept" | "decline";
};

const props = defineProps<{
  organizations: OrgSummary[];
  saving: boolean;
  pendingAction: PendingAction | null;
}>();

const emits = defineEmits<{
  accept: [invitationId: string, organizationId: string];
  decline: [invitationId: string];
}>();

const isOpen = ref(false);

/**
 * Pending organization summaries come from /me so the notification button can
 * render without a separate inbox request during normal app boot.
 */
const pendingInvitations = computed(() =>
  props.organizations.filter((org) => org.status === "Pending"),
);

/**
 * Closes the slideover after the final invitation leaves /me through a parent
 * accept/decline refresh.
 */
watch(
  () => pendingInvitations.value.length,
  (count) => {
    if (count === 0) {
      isOpen.value = false;
    }
  },
);

/** Emits an accept request to the shell component that owns app-store access. */
function emitAccept(
  invitationId: string | null | undefined,
  organizationId: string,
) {
  if (invitationId) {
    emits("accept", invitationId, organizationId);
  }
}

/** Emits a decline request to the shell component that owns app-store access. */
function emitDecline(invitationId: string | null | undefined) {
  if (invitationId) {
    emits("decline", invitationId);
  }
}
</script>
