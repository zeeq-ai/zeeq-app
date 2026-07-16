<template>
  <div class="flex flex-col gap-4">
    <UPageCard
      title="LLM Configuration"
      description="Configure shared model tiers and encrypted tenant-owned API keys for this organization."
      variant="naked"
      orientation="horizontal"
    >
      <div class="flex w-fit gap-2 lg:ms-auto">
        <UButton
          label="Manage keys"
          icon="i-hugeicons-key-01"
          color="neutral"
          variant="ghost"
          :disabled="!canManageOrganization"
          @click="keysOpen = true"
        />
      </div>
    </UPageCard>

    <UAlert
      v-if="!canManageOrganization"
      title="Admin access required"
      description="This view requires admin or owner access."
      icon="i-hugeicons-information-circle"
      color="neutral"
      variant="subtle"
    />

    <template v-else>
      <UAlert
        v-if="llmError"
        title="LLM settings unavailable"
        :description="llmError"
        icon="i-hugeicons-alert-02"
        color="error"
        variant="subtle"
      />

      <div v-if="loading && !draft" class="flex flex-col gap-3">
        <USkeleton v-for="index in 3" :key="index" class="h-32 rounded-md" />
      </div>

      <LlmTierSettingsTable
        v-if="draft"
        :configuration="draft"
        :keys
        :test-results="testResults"
        :testing-tier="testingTier"
        :saving
        :dirty
        @update-tier="updateTier"
        @test="testTier"
        @save="saveSettings"
        @manage-keys="keysOpen = true"
      />

      <LlmKeysSlideover
        v-model:open="keysOpen"
        :keys
        :referenced-key-ids="referencedKeyIds"
        :saving-key-id="savingKeyId"
        @create="createKey"
        @rename="renameKey"
        @rotate="rotateKey"
        @delete="deleteKey"
      />
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import {
  defaultLlmConfiguration,
  llmProviderCapabilities,
  llmTierNames,
  type LlmSettingsConfiguration,
  type LlmTierName,
  type LlmTierSettings,
  useLlmSettingsStore,
} from "@/stores/llm-settings-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import type { SaveLlmSettingsRequest } from "@/api/generated/types/SaveLlmSettingsRequest";

import LlmKeysSlideover from "./LlmKeysSlideover.vue";
import LlmTierSettingsTable from "./LlmTierSettingsTable.vue";

const toast = useToast();
const organizationSettingsStore = useOrganizationSettingsStore();
const llmSettingsStore = useLlmSettingsStore();
const { canManageOrganization } = storeToRefs(organizationSettingsStore);
const {
  configuration,
  keys,
  loading,
  saving,
  savingKeyId,
  testingTier,
  testResults,
  error: llmError,
} = storeToRefs(llmSettingsStore);

const draft = ref<LlmSettingsConfiguration | null>(null);
const savedSnapshot = ref("");
const keysOpen = ref(false);

/** Dirty state compares the current editable draft with the last loaded/saved snapshot. */
const dirty = computed(() => {
  if (!draft.value) {
    return false;
  }

  return serializeConfiguration(draft.value) !== savedSnapshot.value;
});

/** Key references block accidental deletion of keys currently selected by any tier. */
const referencedKeyIds = computed(() => {
  if (!draft.value) {
    return [];
  }

  return llmTierNames
    .map((tier) => draft.value?.[tier].keyId ?? null)
    .filter((keyId): keyId is string => Boolean(keyId));
});

/**
 * Loading is gated by membership role from /me so non-admin members can see the
 * route notice without reading LLM settings or key metadata.
 */
onMounted(async () => {
  if (!canManageOrganization.value) {
    return;
  }

  await loadSettings();
});

/** Keeps the local draft in sync after load/save without sharing mutable store state. */
watch(
  configuration,
  (value) => {
    const next = cloneConfiguration(value ?? defaultLlmConfiguration());
    draft.value = next;
    savedSnapshot.value = serializeConfiguration(next);
  },
  { immediate: true },
);

/** If the active user gains manager context after auth refresh, load the manager view. */
watch(canManageOrganization, async (canManage) => {
  if (!canManage) {
    return;
  }

  await loadSettings();
});

/** Loads LLM settings and reports API failures through the shared toast surface. */
async function loadSettings() {
  try {
    await llmSettingsStore.loadLlmSettings();
  } catch (err: unknown) {
    showError("Could not load LLM settings", err);
  }
}

/** Updates one tier in the editable draft. */
function updateTier(
  tier: LlmTierName,
  value: { provider: string; model: string; keyId: string | null; endpoint: string | null },
) {
  if (!draft.value) {
    return;
  }

  draft.value = {
    ...draft.value,
    [tier]: { ...value },
  };
}

/** Saves all tier selections in a single organization configuration request. */
async function saveSettings() {
  if (!draft.value) {
    return;
  }

  const managedKeyError = managedKeyRequirementError(draft.value);
  if (managedKeyError) {
    showManagedKeyWarning(managedKeyError);
    return;
  }

  try {
    await llmSettingsStore.saveLlmSettings(toSaveRequest(draft.value));
    toast.add({
      title: "LLM settings saved",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not save LLM settings", err);
  }
}

/** Runs the bounded provider/key access check for the current tier draft. */
async function testTier(tier: LlmTierName) {
  if (!draft.value) {
    return;
  }

  const settings = draft.value[tier];
  const managedKeyError = tierManagedKeyRequirementError(tier, settings);
  if (managedKeyError) {
    showManagedKeyWarning(managedKeyError);
    return;
  }

  try {
    const result = await llmSettingsStore.testLlmConfiguration({
      tier,
      provider: settings.provider,
      model: settings.model,
      keyId: settings.keyId,
      prompt: null,
      endpoint: settings.endpoint,
    });

    toast.add({
      title: result?.success
        ? "LLM configuration test succeeded"
        : "LLM configuration test failed",
      description: result?.message,
      icon: result?.success ? "i-hugeicons-tick-02" : "i-hugeicons-alert-02",
      color: result?.success ? "success" : "error",
    });

    if (result?.success) {
      await saveSettings();
    }
  } catch (err: unknown) {
    showError("Could not test LLM configuration", err);
  }
}

/** Creates a tenant-owned key and never stores plaintext beyond the submit call. */
async function createKey(payload: { name: string | null; apiKey: string }) {
  try {
    await llmSettingsStore.createKey(payload);
    toast.add({
      title: "LLM key added",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not add LLM key", err);
  }
}

/** Renames key metadata without changing encrypted bytes. */
async function renameKey(keyId: string, name: string | null) {
  try {
    await llmSettingsStore.renameKey(keyId, { name });
    toast.add({
      title: "LLM key renamed",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not rename LLM key", err);
  }
}

/** Rotates an existing key's encrypted value. */
async function rotateKey(keyId: string, apiKey: string) {
  try {
    await llmSettingsStore.rotateKey(keyId, { apiKey });
    toast.add({
      title: "LLM key rotated",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not rotate LLM key", err);
  }
}

/** Deletes an unreferenced key after the child confirmation emits. */
async function deleteKey(keyId: string) {
  if (referencedKeyIds.value.includes(keyId)) {
    toast.add({
      title: "Key is in use",
      description: "Choose another key for every tier before deleting it.",
      icon: "i-hugeicons-information-circle",
      color: "warning",
    });
    return;
  }

  try {
    await llmSettingsStore.deleteKey(keyId);
    toast.add({
      title: "LLM key deleted",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not delete LLM key", err);
  }
}

/** Creates a detached configuration copy for local editing. */
function cloneConfiguration(
  value: LlmSettingsConfiguration,
): LlmSettingsConfiguration {
  return {
    fast: { ...value.fast },
    high: { ...value.high },
    max: { ...value.max },
  };
}

/** Converts local state into the generated save request shape. */
function toSaveRequest(
  value: LlmSettingsConfiguration,
): SaveLlmSettingsRequest {
  const cloned = cloneConfiguration(value);

  // Authoritative endpoint normalization at the save boundary: strip any endpoint for providers
  // that do not require one. The interactive `updateProvider` already clears it on switch, but the
  // endpoint input is hidden for non-endpoint providers, so a stale endpoint carried in from loaded
  // config would otherwise be silently persisted with no way for the user to see or clear it.
  for (const tier of llmTierNames) {
    if (!llmProviderCapabilities(cloned[tier].provider).requiresEndpoint) {
      cloned[tier] = { ...cloned[tier], endpoint: null };
    }
  }

  return cloned;
}

/** Returns the first tier that needs a tenant-managed key before save. */
function managedKeyRequirementError(
  value: LlmSettingsConfiguration,
): string | null {
  for (const tier of llmTierNames) {
    const error = tierManagedKeyRequirementError(tier, value[tier]);
    if (error) {
      return error;
    }
  }

  return null;
}

/** Enforces the product policy that providers needing managed keys have one, and endpoint-required providers have one. */
function tierManagedKeyRequirementError(
  tier: LlmTierName,
  settings: LlmTierSettings,
): string | null {
  const capabilities = llmProviderCapabilities(settings.provider);

  if (capabilities.requiresManagedKey && !settings.keyId) {
    return `${tierLabel(tier)} requires a managed API key for ${settings.provider}.`;
  }

  if (capabilities.requiresEndpoint && !settings.endpoint?.trim()) {
    return `${tierLabel(tier)} requires an endpoint URL for ${settings.provider}.`;
  }

  return null;
}

/** Converts internal tier IDs into user-facing labels. */
function tierLabel(tier: LlmTierName): string {
  return tier.charAt(0).toUpperCase() + tier.slice(1);
}

/** Serializes tier settings in stable key order for dirty checking. */
function serializeConfiguration(value: LlmSettingsConfiguration): string {
  return JSON.stringify({
    fast: value.fast,
    high: value.high,
    max: value.max,
  });
}

/** Shows API failures in a consistent toast shape. */
function showError(title: string, err: unknown) {
  toast.add({
    title,
    description: err instanceof Error ? err.message : "LLM settings failed.",
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}

/** Shows a consistent warning when provider policy requires a managed key. */
function showManagedKeyWarning(description: string) {
  toast.add({
    title: "Managed key required",
    description,
    icon: "i-hugeicons-information-circle",
    color: "warning",
  });
}
</script>
