<template>
  <UPageCard variant="subtle" :ui="{ container: 'p-0 sm:p-0 gap-y-0' }">
    <!-- LLM configuration is intentionally view-only for system organization management. -->
    <div class="divide-y divide-default">
      <div
        v-for="tier in tiers"
        :key="tier.value"
        class="grid gap-3 p-4 sm:grid-cols-[8rem_minmax(0,1fr)]"
      >
        <div>
          <p class="text-sm font-semibold text-highlighted">{{ tier.label }}</p>
          <UBadge
            :label="
              tier.configuration.usesManagedKey ? 'Managed key' : 'Default key'
            "
            :color="tier.configuration.usesManagedKey ? 'neutral' : 'warning'"
            variant="subtle"
            class="mt-2"
          />
        </div>

        <dl class="grid min-w-0 gap-3 sm:grid-cols-2">
          <div>
            <dt class="text-xs font-medium text-muted">Provider</dt>
            <dd class="mt-1 truncate text-sm text-default">
              {{ tier.configuration.provider }}
            </dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-muted">Model</dt>
            <dd class="mt-1 truncate text-sm text-default">
              {{ tier.configuration.model }}
            </dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-muted">Endpoint</dt>
            <dd class="mt-1 truncate text-sm text-default">
              {{ tier.configuration.endpoint ?? "Default" }}
            </dd>
          </div>
          <div>
            <dt class="text-xs font-medium text-muted">Key ID</dt>
            <dd class="mt-1 truncate text-sm text-default">
              {{ tier.configuration.keyId ?? "Internal default" }}
            </dd>
          </div>
        </dl>
      </div>
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import type { SystemOrganizationLlmConfigurationResponse } from "@/api/generated";

const props = defineProps<{
  configuration: SystemOrganizationLlmConfigurationResponse;
}>();

/** Converts the fixed fast/high/max response shape into a renderable tier list. */
const tiers = computed(() => [
  { value: "fast", label: "Fast", configuration: props.configuration.fast },
  { value: "high", label: "High", configuration: props.configuration.high },
  { value: "max", label: "Max", configuration: props.configuration.max },
]);
</script>
