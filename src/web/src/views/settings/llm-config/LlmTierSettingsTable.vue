<template>
  <!-- Tier table keeps row edits local to the root draft and emits every action upward. -->
  <UPageCard
    variant="subtle"
    :ui="{
      container: 'p-0 sm:p-0 gap-y-0',
    }"
  >
    <div
      class="flex flex-wrap items-center justify-between gap-3 border-b border-default p-4"
    >
      <div>
        <h2 class="text-base font-semibold text-highlighted">Model tiers</h2>
        <p class="mt-1 text-sm text-muted">
          Choose provider, model, and default or tenant-owned key per workload
          tier.
        </p>
      </div>

      <UButton
        label="Save changes"
        icon="i-hugeicons-floppy-disk"
        color="neutral"
        variant="subtle"
        :loading="saving"
        :disabled="!dirty || saving || hasManagedKeyRequirementErrors"
        @click="emits('save')"
      />
    </div>

    <UAccordion
      type="multiple"
      :default-value="[]"
      :items="accordionItems"
      :ui="{
        root: 'divide-y divide-default',
        item: 'p-0',
        trigger: 'px-4 py-4 sm:px-6 hover:bg-elevated/40',
        content: 'px-4 pb-5 sm:px-6 sm:pb-6',
        body: 'grid gap-4 pt-0',
      }"
    >
      <template #default="{ item }">
        <div class="min-w-0">
          <div class="flex flex-wrap items-center gap-2">
            <span class="text-sm font-semibold text-highlighted">
              {{ item.label }}
            </span>

            <UBadge
              :label="llmModelLabel(configuration[item.value].model)"
              icon="i-hugeicons-ai-magic"
              color="neutral"
              variant="outline"
              class="max-w-full rounded-full"
            />

            <UBadge
              v-if="tierViewModels[item.value].requiresManagedKey"
              :label="
                tierViewModels[item.value].managedKeyMissing
                  ? 'Requires managed key'
                  : 'Uses managed key'
              "
              icon="i-hugeicons-key-01"
              :color="
                tierViewModels[item.value].managedKeyMissing
                  ? 'error'
                  : 'neutral'
              "
              variant="subtle"
              class="rounded-full"
            />

            <UBadge
              v-if="testResults[item.value]"
              :label="
                testResults[item.value]?.success ? 'Test passed' : 'Test failed'
              "
              :icon="
                testResults[item.value]?.success
                  ? 'i-hugeicons-tick-02'
                  : 'i-hugeicons-alert-02'
              "
              :color="testResults[item.value]?.success ? 'success' : 'error'"
              variant="subtle"
              class="rounded-full"
            />
          </div>
          <p class="mt-1 text-sm text-muted">
            {{ item.description }}
          </p>
        </div>
      </template>

      <template #body="{ item }">
        <div
          class="grid gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1fr)_auto] lg:items-end"
        >
          <UFormField label="Provider" required>
            <USelect
              :model-value="configuration[item.value].provider"
              :items="providerItems"
              color="neutral"
              :disabled="saving"
              class="w-full"
              @update:model-value="updateProvider(item.value, $event)"
            />
          </UFormField>

          <UFormField label="Model" required>
            <USelect
              :model-value="configuration[item.value].model"
              :items="tierViewModels[item.value].modelItems"
              color="neutral"
              :disabled="saving"
              class="w-full"
              @update:model-value="updateModel(item.value, $event)"
            />
          </UFormField>

          <UFormField label="API key" required>
            <USelect
              :model-value="tierViewModels[item.value].keySelectValue"
              :items="tierViewModels[item.value].keyItems"
              :placeholder="tierViewModels[item.value].keyPlaceholder"
              color="neutral"
              :disabled="saving"
              class="w-full"
              @update:model-value="updateKey(item.value, $event)"
            />
          </UFormField>

          <UButton
            label="Test"
            icon="i-hugeicons-test-tube-01"
            color="neutral"
            variant="soft"
            class="justify-self-end"
            :loading="testingTier === item.value"
            :disabled="
              saving ||
              testingTier !== null ||
              tierViewModels[item.value].managedKeyMissing ||
              tierViewModels[item.value].endpointMissing
            "
            @click="emits('test', item.value)"
          />
        </div>

        <div v-if="tierViewModels[item.value].requiresEndpoint">
          <UFormField
            label="Endpoint"
            description="Your Azure OpenAI resource URL, e.g. https://my-instance.openai.azure.com/"
            required
          >
            <UInput
              :model-value="configuration[item.value].endpoint ?? ''"
              placeholder="https://my-instance.openai.azure.com/"
              color="neutral"
              :disabled="saving"
              class="w-full font-mono text-sm"
              @update:model-value="updateEndpoint(item.value, $event)"
            />
          </UFormField>
        </div>

        <UAlert
          v-if="testResults[item.value]"
          :title="
            testResults[item.value]?.success
              ? 'Configuration test succeeded'
              : 'Configuration test failed'
          "
          :description="testDescription(item.value)"
          :icon="
            testResults[item.value]?.success
              ? 'i-hugeicons-tick-02'
              : 'i-hugeicons-alert-02'
          "
          :color="testResults[item.value]?.success ? 'success' : 'error'"
          variant="subtle"
        />
      </template>
    </UAccordion>
  </UPageCard>
</template>

<script setup lang="ts">
import {
  isLlmProvider,
  llmKeyDisplayName,
  llmModelCatalog,
  llmModelDefaults,
  llmModelLabel,
  llmProviderCapabilities,
  llmTierNames,
  type LlmApiKey,
  type LlmProviderAccessTestResult,
  type LlmSettingsConfiguration,
  type LlmTierName,
} from "@/stores/llm-settings-store";

const defaultKeyValue = "__internal-default__";
const addManagedKeyValue = "__add-managed-key__";

const props = defineProps<{
  configuration: LlmSettingsConfiguration;
  keys: LlmApiKey[];
  testResults: Record<LlmTierName, LlmProviderAccessTestResult | null>;
  testingTier: LlmTierName | null;
  saving: boolean;
  dirty: boolean;
}>();

const emits = defineEmits<{
  updateTier: [
    tier: LlmTierName,
    value: {
      provider: string;
      model: string;
      keyId: string | null;
      endpoint: string | null;
    },
  ];
  test: [tier: LlmTierName];
  save: [];
  manageKeys: [];
}>();

const tiers = llmTierNames;
const providerItems = Object.keys(llmModelCatalog).map((provider) => ({
  label: provider,
  value: provider,
}));
const tierLabels: Record<LlmTierName, string> = {
  fast: "Fast",
  high: "High",
  max: "Max",
};
const tierDescriptions: Record<LlmTierName, string> = {
  fast: "Low-latency calls where cost and responsiveness matter most.",
  high: "Higher quality work for review, synthesis, and harder reasoning.",
  max: "Most capable model for expensive or complex workflows.",
};
const accordionItems = tiers.map((tier) => ({
  value: tier,
  label: tierLabels[tier],
  description: tierDescriptions[tier],
}));

type SelectItem = { label: string; value: string };

type TierViewModel = {
  modelItems: SelectItem[];
  keyItems: SelectItem[];
  keySelectValue: string | undefined;
  keyPlaceholder: string;
  canUseInternalDefault: boolean;
  requiresManagedKey: boolean;
  managedKeyMissing: boolean;
  requiresEndpoint: boolean;
  endpointMissing: boolean;
};

/** Cached per-tier view model derived from configuration + keys. */
const tierViewModels = computed<Record<LlmTierName, TierViewModel>>(() => {
  const result = {} as Record<LlmTierName, TierViewModel>;

  for (const tier of tiers) {
    const settings = props.configuration[tier];
    const capabilities = llmProviderCapabilities(settings.provider);
    const canUseInternalDefault = !capabilities.requiresManagedKey;
    const keyMissing = capabilities.requiresManagedKey && !settings.keyId;
    const requiresEndpoint = capabilities.requiresEndpoint;
    const endpointMissing = requiresEndpoint && !settings.endpoint?.trim();

    // Model items
    const catalogValues = isLlmProvider(settings.provider)
      ? [...llmModelCatalog[settings.provider]]
      : [];
    const modelValues = catalogValues.some((m) => m === settings.model)
      ? catalogValues
      : [settings.model, ...catalogValues].filter(Boolean);
    const modelItems: SelectItem[] = modelValues.map((model) => ({
      label: llmModelLabel(model),
      value: model,
    }));

    // Key items
    const defaultKeyItems: SelectItem[] = canUseInternalDefault
      ? [{ label: "Internal default key", value: defaultKeyValue }]
      : [];
    const managedKeyItems: SelectItem[] = props.keys.map((key) => ({
      label: llmKeyDisplayName(key),
      value: key.id,
    }));
    const addKeyItems: SelectItem[] =
      props.keys.length === 0
        ? [{ label: "(Add managed key)", value: addManagedKeyValue }]
        : [];
    const keyItems = [...defaultKeyItems, ...managedKeyItems, ...addKeyItems];

    // Key select value
    const keyId = settings.keyId;
    const keySelectValue = keyId
      ? keyId
      : canUseInternalDefault
        ? defaultKeyValue
        : undefined;

    result[tier] = {
      modelItems,
      keyItems,
      keySelectValue,
      keyPlaceholder: canUseInternalDefault
        ? "Internal default key"
        : "Choose managed key",
      canUseInternalDefault,
      requiresManagedKey: capabilities.requiresManagedKey,
      managedKeyMissing: keyMissing,
      requiresEndpoint,
      endpointMissing,
    };
  }

  return result;
});

/** Save is blocked when a managed key or required endpoint is absent. */
const hasManagedKeyRequirementErrors = computed(() =>
  tiers.some(
    (tier) =>
      tierViewModels.value[tier].managedKeyMissing ||
      tierViewModels.value[tier].endpointMissing,
  ),
);

/** Applies provider changes and resets model to that provider's tier default. Clears endpoint when switching away from Azure OpenAI. */
function updateProvider(tier: LlmTierName, value: unknown) {
  if (typeof value !== "string") {
    return;
  }

  const model = isLlmProvider(value)
    ? llmModelDefaults[value][tier]
    : props.configuration[tier].model;

  const endpoint = llmProviderCapabilities(value).requiresEndpoint
    ? props.configuration[tier].endpoint
    : null;

  emits("updateTier", tier, {
    ...props.configuration[tier],
    provider: value,
    model,
    keyId: props.configuration[tier].keyId,
    endpoint,
  });
}

/** Emits endpoint changes from the input field. */
function updateEndpoint(tier: LlmTierName, value: unknown) {
  emits("updateTier", tier, {
    ...props.configuration[tier],
    endpoint: typeof value === "string" && value.trim() ? value.trim() : null,
  });
}

/** Emits model changes from the select component. */
function updateModel(tier: LlmTierName, value: unknown) {
  if (typeof value !== "string") {
    return;
  }

  emits("updateTier", tier, { ...props.configuration[tier], model: value });
}

/** Converts the internal default key option back to a null API key reference. */
function updateKey(tier: LlmTierName, value: unknown) {
  if (typeof value !== "string") {
    return;
  }

  if (value === addManagedKeyValue) {
    emits("manageKeys");
    return;
  }

  emits("updateTier", tier, {
    ...props.configuration[tier],
    keyId: value === defaultKeyValue ? null : value,
  });
}

/** Shows the sanitized backend message plus latency, never raw model output. */
function testDescription(tier: LlmTierName): string {
  const result = props.testResults[tier];
  if (!result) {
    return "";
  }

  const latency = result.latencyMs > 0 ? ` (${result.latencyMs} ms)` : "";

  return `${result.message}${latency}`;
}
</script>
