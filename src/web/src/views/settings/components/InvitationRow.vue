<template>
  <!-- Pending invitation row for the signed-in user's membership queue. -->
  <li class="flex items-center justify-between gap-3 px-4 py-3 sm:px-6">
    <div class="min-w-0">
      <p class="truncate text-sm font-medium text-highlighted">
        {{ invitation.organizationName || invitation.organizationId }}
      </p>
      <p class="truncate text-sm text-muted">
        Invited as {{ invitation.role }}
      </p>
    </div>

    <div class="flex shrink-0 items-center gap-2">
      <UButton
        label="Join"
        icon="i-hugeicons-tick-02"
        color="neutral"
        :disabled="saving"
        @click="emits('accept', invitation.id, invitation.organizationId)"
      />
      <UButton
        label="Decline"
        icon="i-hugeicons-cancel-01"
        color="neutral"
        variant="ghost"
        :disabled="saving"
        @click="emits('decline', invitation.id)"
      />
    </div>
  </li>
</template>

<script setup lang="ts">
import type { InvitationResponse } from "@/api/generated/types/InvitationResponse";

defineProps<{
  invitation: InvitationResponse;
  saving: boolean;
}>();

const emits = defineEmits<{
  accept: [invitationId: string, organizationId: string];
  decline: [invitationId: string];
}>();
</script>
