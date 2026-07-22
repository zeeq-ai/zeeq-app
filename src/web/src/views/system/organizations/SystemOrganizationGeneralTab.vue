<template>
  <div class="flex flex-col gap-4">
    <!-- Only activation and service tier are mutable in this phase. -->
    <UPageCard variant="subtle" :ui="{ container: 'p-0 sm:p-0 gap-y-0' }">
      <div class="divide-y divide-default">
        <div
          class="grid gap-3 px-4 py-4 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center sm:px-6"
        >
          <div class="min-w-0">
            <p class="text-sm font-medium text-highlighted">Active</p>
            <p class="mt-1 text-sm text-muted">
              Allow members to access organization-scoped features.
            </p>
          </div>
          <USwitch v-model="activeDraft" :disabled="saving" />
        </div>

        <div
          class="grid gap-3 px-4 py-4 sm:grid-cols-[minmax(0,1fr)_16rem] sm:items-center sm:px-6"
        >
          <div class="min-w-0">
            <p class="text-sm font-medium text-highlighted">Service tier</p>
            <p class="mt-1 text-sm text-muted">
              Controls tenant queue capacity for organization-owned work.
            </p>
          </div>
          <USelect
            v-model="tierDraft"
            :items="tierItems"
            color="neutral"
            :disabled="saving"
            class="w-full sm:max-w-64"
          />
        </div>
      </div>
    </UPageCard>

    <div class="flex justify-end">
      <UButton
        label="Save changes"
        icon="i-hugeicons-floppy-disk"
        color="neutral"
        variant="outline"
        :loading="saving"
        :disabled="!dirty || saving"
        @click="emits('save', { active: activeDraft, tier: tierDraft })"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { SystemOrganizationDetailsResponse } from "@/api/generated";
import {
  systemOrganizationTierOptions,
  type SystemOrganizationTier,
} from "@/stores/system-org-management-store";
import { isSystemOrganizationActive } from "./organization-management";

const props = defineProps<{
  organization: SystemOrganizationDetailsResponse;
  saving: boolean;
}>();

const emits = defineEmits<{
  save: [request: { active: boolean; tier: SystemOrganizationTier }];
}>();

/** Local drafts avoid mutating store state before the save action succeeds. */
const activeDraft = ref(isSystemOrganizationActive(props.organization));
const tierDraft = ref(toTier(props.organization.tier));
/** Tier options mirror the backend enum names accepted by PATCH validation. */
const tierItems = systemOrganizationTierOptions.map((tier) => ({
  label: tier,
  value: tier,
}));

/** Enables saving only when the local draft differs from the latest response. */
const dirty = computed(
  () =>
    activeDraft.value !== isSystemOrganizationActive(props.organization) ||
    tierDraft.value !== toTier(props.organization.tier),
);

/** Resets drafts when the selected organization changes or a save returns fresh state. */
watch(
  () => props.organization,
  (organization) => {
    activeDraft.value = isSystemOrganizationActive(organization);
    tierDraft.value = toTier(organization.tier);
  },
);

/** Normalizes backend tier casing to one of the UI enum options. */
function toTier(tier: string): SystemOrganizationTier {
  const match = systemOrganizationTierOptions.find(
    (option) => option.toLowerCase() === tier.toLowerCase(),
  );

  return match ?? "Default";
}
</script>
