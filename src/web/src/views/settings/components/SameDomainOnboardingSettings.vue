<template>
  <!--
    Same-domain onboarding settings panel. This child owns only local display
    state and emits typed update requests to the root settings view.
  -->
  <UPageCard variant="subtle">
    <div class="grid gap-5">
      <!-- Enablement summary and switch derived from backend eligibility fields. -->
      <div class="grid gap-4 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-start">
        <div class="min-w-0">
          <div class="flex items-center gap-2">
            <UIcon name="i-hugeicons-mail-account-01" class="size-5" />
            <h2 class="text-base font-semibold text-highlighted">
              Same-domain onboarding
            </h2>
          </div>
          <p class="mt-1 text-sm text-muted">
            {{ statusText }}
          </p>
        </div>

        <USwitch
          :model-value="enabled"
          :disabled="switchDisabled"
          @update:model-value="updateEnabled"
        />
      </div>

      <!-- Backend block reasons explain why the owner/admin cannot enable now. -->
      <UAlert
        v-if="blockReason && !enabled"
        icon="i-hugeicons-alert-02"
        color="warning"
        variant="subtle"
        :description="blockReason"
      />

      <USeparator />

      <!-- Default role selector aligns with the right-side controls in this settings page. -->
      <div class="grid gap-4 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-start">
        <div class="min-w-0">
          <h3 class="text-sm font-medium text-highlighted">Default role</h3>
          <p class="mt-1 text-sm text-muted">
            Applied to invitations created from the claimed domain.
          </p>
        </div>

        <USelect
          :model-value="selectedDefaultRole"
          :items="roleItems"
          aria-label="Default role"
          color="neutral"
          class="w-full sm:w-40"
          :disabled="!canManage || saving || !enabled"
          @update:model-value="updateDefaultRole"
        />
      </div>
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import type { OrganizationResponse } from "@/api/generated/types/OrganizationResponse";
import {
  sameDomainOnboardingRoleOptions,
  type SameDomainOnboardingRole,
} from "@/stores/organization-settings-store";

const props = defineProps<{
  organization: OrganizationResponse | null;
  canManage: boolean;
  saving: boolean;
}>();

const emits = defineEmits<{
  update: [
    request: {
      enabled: boolean;
      defaultRole?: SameDomainOnboardingRole | null;
    },
  ];
}>();

// These computed refs mirror the backend response shape so UI disabled states
// stay aligned with server-side enforcement.
const enabled = computed(
  () => props.organization?.autoInviteSameDomainEnabled === true,
);
const canEnable = computed(
  () => props.organization?.autoInviteSameDomainCanEnable === true,
);
const domain = computed(() => props.organization?.autoInviteSameDomain ?? null);
const blockReason = computed(
  () => props.organization?.autoInviteSameDomainBlockReason ?? null,
);
const selectedDefaultRole = computed(() =>
  toSameDomainOnboardingRole(props.organization?.autoInviteDefaultRole),
);
const switchDisabled = computed(
  () =>
    !props.canManage || props.saving || (!enabled.value && !canEnable.value),
);
const statusText = computed(() => {
  if (enabled.value && domain.value) {
    return `${domain.value} is enabled`;
  }

  if (!props.canManage) {
    return "View only";
  }

  return "Disabled";
});

const roleItems = sameDomainOnboardingRoleOptions.map((role) => ({
  label: role,
  value: role,
}));

// The backend derives/clears the domain when the switch changes; the frontend
// sends only intent plus the selected default role.
function updateEnabled(nextValue: boolean) {
  emits("update", {
    enabled: nextValue,
    defaultRole: selectedDefaultRole.value,
  });
}

// Role changes are saved immediately through the root view/store, matching the
// rest of the compact settings controls in this area.
function updateDefaultRole(nextValue: unknown) {
  if (typeof nextValue !== "string") {
    return;
  }

  const defaultRole = toSameDomainOnboardingRole(nextValue);
  if (!defaultRole || defaultRole === selectedDefaultRole.value) {
    return;
  }

  emits("update", {
    enabled: enabled.value,
    defaultRole,
  });
}

// Generated OpenAPI types expose role as string; narrow to the two values this
// settings panel can submit and fall back to the backend default.
function toSameDomainOnboardingRole(
  role: string | null | undefined,
): SameDomainOnboardingRole {
  return role === "admin" || role === "member" ? role : "member";
}
</script>
