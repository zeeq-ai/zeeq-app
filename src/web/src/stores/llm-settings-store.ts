import { defineStore, acceptHMRUpdate, storeToRefs } from "pinia";
import { LLMSettings } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import type { CreateLlmApiKeyRequest } from "@/api/generated/types/CreateLlmApiKeyRequest";
import type { RenameLlmApiKeyRequest } from "@/api/generated/types/RenameLlmApiKeyRequest";
import type { RotateLlmApiKeyRequest } from "@/api/generated/types/RotateLlmApiKeyRequest";
import type { SaveLlmSettingsRequest } from "@/api/generated/types/SaveLlmSettingsRequest";
import type { TestLlmSettingsRequest } from "@/api/generated/types/TestLlmSettingsRequest";

/**
 * LLM tier names exposed by the organization settings API.
 */
export const llmTierNames = ["fast", "high", "max"] as const;

export type LlmTierName = (typeof llmTierNames)[number];

/**
 * First-phase provider catalog rendered by the web app.
 *
 * The backend intentionally accepts any non-empty model string; this catalog is
 * product UI guidance, not a server-side allowlist.
 */
export const llmModelCatalog = {
  Fireworks: [
    "accounts/fireworks/models/glm-5p2",
    "accounts/fireworks/models/deepseek-v4-pro",
    "accounts/fireworks/models/deepseek-v4-flash",
  ],
  OpenAI: [
    "gpt-5.4-mini",
    "gpt-5.6-luna",
    "gpt-5.6-terra",
    "gpt-5.6-sol",
    "gpt-5.4",
    "gpt-5.5",
  ],
  "Azure OpenAI": [
    "gpt-5.4-mini",
    "gpt-5.6-luna",
    "gpt-5.6-terra",
    "gpt-5.6-sol",
    "gpt-5.4",
    "gpt-5.5",
  ],
  Anthropic: [
    "claude-haiku-4-5",
    "claude-sonnet-4-6",
    "claude-opus-4-6",
    "claude-opus-4-7",
    "claude-opus-4-8",
  ],
} as const;

export type LlmProvider = keyof typeof llmModelCatalog;

/**
 * Human-friendly labels for stored model ids; non-mapped ids display verbatim.
 */
export const llmModelLabels: Record<string, string> = {
  // Fireworks
  "accounts/fireworks/models/glm-5p2": "GLM 5.2",
  "accounts/fireworks/models/deepseek-v4-pro": "DeepSeek v4 Pro (Fireworks)",
  "accounts/fireworks/models/deepseek-v4-flash":
    "DeepSeek v4 Flash (Fireworks)",
  // OpenAI
  "gpt-5.4-mini": "GPT 5.4 Mini",
  "gpt-5.4": "GPT 5.4",
  "gpt-5.5": "GPT 5.5",
  "gpt-5.6-luna": "GPT 5.6 Luna",
  "gpt-5.6-terra": "GPT 5.6 Terra",
  "gpt-5.6-sol": "GPT 5.6 Sol",
  // Anthropic
  "claude-haiku-4-5": "Claude Haiku 4.5",
  "claude-sonnet-4-6": "Claude Sonnet 4.6",
  "claude-opus-4-6": "Claude Opus 4.6",
  "claude-opus-4-7": "Claude Opus 4.7",
  "claude-opus-4-8": "Claude Opus 4.8",
};

/**
 * Maps a stored model id to its display label, falling back to the id.
 */
export function llmModelLabel(model: string): string {
  return llmModelLabels[model] ?? model;
}

/**
 * Tier defaults applied when the user changes provider in the UI.
 */
export const llmModelDefaults: Record<
  LlmProvider,
  Record<LlmTierName, string>
> = {
  Fireworks: {
    fast: "accounts/fireworks/models/glm-5p2",
    high: "accounts/fireworks/models/glm-5p2",
    max: "accounts/fireworks/models/glm-5p2",
  },
  OpenAI: {
    fast: "gpt-5.4-mini",
    high: "gpt-5.6-luna",
    max: "gpt-5.5",
  },
  "Azure OpenAI": {
    fast: "gpt-5.4-mini",
    high: "gpt-5.6-luna",
    max: "gpt-5.5",
  },
  Anthropic: {
    fast: "claude-haiku-4-5",
    high: "claude-sonnet-4-6",
    max: "claude-opus-4-6",
  },
};

export type LlmTierSettings = {
  provider: string;
  model: string;
  keyId: string | null;
  endpoint: string | null;
};

export type LlmSettingsConfiguration = Record<LlmTierName, LlmTierSettings>;

/**
 * Tenant-owned encrypted key metadata. Plaintext key values are write-only and
 * are never represented by this store.
 */
export type LlmApiKey = {
  id: string;
  name: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
};

export type LlmSettingsView = {
  canManage: boolean;
  notice: string | null;
  configuration: LlmSettingsConfiguration | null;
  keys: LlmApiKey[];
};

export type LlmProviderAccessTestResult = {
  success: boolean;
  provider: string;
  model: string;
  latencyMs: number;
  errorCode: string | null;
  message: string;
};

/**
 * Store for organization LLM settings.
 *
 * Generated OpenAPI response payloads are typed, but date-time fields still
 * arrive as JSON strings through the fetch transport. This store owns the
 * string-safe view model and keeps child components away from normalization
 * details.
 */
export const useLlmSettingsStore = defineStore("llm-settings-store", () => {
  const appStore = useAppStore();
  const { user: me } = storeToRefs(appStore);

  const view = ref<LlmSettingsView | null>(null);
  const configuration = ref<LlmSettingsConfiguration | null>(null);
  const keys = ref<LlmApiKey[]>([]);
  const loading = ref(false);
  const saving = ref(false);
  const savingKeyId = ref<string | null>(null);
  const testingTier = ref<LlmTierName | null>(null);
  const testResults = ref<
    Record<LlmTierName, LlmProviderAccessTestResult | null>
  >({
    fast: null,
    high: null,
    max: null,
  });
  const error = ref<string | null>(null);

  const currentOrganizationId = computed(
    () => me.value?.organizationId ?? null,
  );
  const canManage = computed(() => view.value?.canManage ?? false);
  const notice = computed(() => view.value?.notice ?? null);

  /**
   * Loads the active organization's LLM configuration and encrypted key
   * metadata through the generated API client.
   */
  async function loadLlmSettings() {
    if (!currentOrganizationId.value) {
      clearSettings();
      return;
    }

    loading.value = true;
    error.value = null;

    try {
      const response = await LLMSettings.getOrganizationLlmSettings(
        currentOrganizationId.value,
      );
      setView(normalizeView(response));
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      loading.value = false;
    }
  }

  /**
   * Reloads key metadata without overwriting the in-progress tier configuration
   * so that adding or modifying keys does not reset unsaved provider/model/key
   * selections.
   */
  async function loadKeys() {
    if (!currentOrganizationId.value) {
      return;
    }

    loading.value = true;
    error.value = null;

    try {
      const response = await LLMSettings.getOrganizationLlmSettings(
        currentOrganizationId.value,
      );
      const normalized = normalizeView(response);
      view.value = normalized;
      keys.value = normalized.keys;
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      loading.value = false;
    }
  }

  /**
   * Saves provider/model/key selections for all three tiers.
   */
  async function saveLlmSettings(request: SaveLlmSettingsRequest) {
    if (!currentOrganizationId.value) {
      return;
    }

    saving.value = true;
    error.value = null;

    try {
      const response = await LLMSettings.saveOrganizationLlmSettings(
        currentOrganizationId.value,
        request,
      );
      setView(normalizeView(response));
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      saving.value = false;
    }
  }

  /**
   * Runs the bounded backend access check for one candidate tier selection.
   */
  async function testLlmConfiguration(request: TestLlmSettingsRequest) {
    if (!currentOrganizationId.value || !isLlmTierName(request.tier)) {
      return null;
    }

    testingTier.value = request.tier;
    error.value = null;
    testResults.value[request.tier] = null;

    try {
      const response = await LLMSettings.testOrganizationLlmSettings(
        currentOrganizationId.value,
        request,
      );
      const result = normalizeTestResult(response);
      testResults.value[request.tier] = result;

      return result;
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      testingTier.value = null;
    }
  }

  /**
   * Creates a tenant-owned encrypted API key and refreshes metadata. Plaintext
   * lives only in the caller's transient form state.
   */
  async function createKey(request: CreateLlmApiKeyRequest) {
    if (!currentOrganizationId.value) {
      return null;
    }

    savingKeyId.value = "new";
    error.value = null;

    try {
      const response = await LLMSettings.createOrganizationLlmApiKey(
        currentOrganizationId.value,
        request,
      );
      const key = normalizeKey(response);
      await loadKeys();

      return key;
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      savingKeyId.value = null;
    }
  }

  /**
   * Renames a tenant-owned encrypted API key without touching ciphertext.
   */
  async function renameKey(keyId: string, request: RenameLlmApiKeyRequest) {
    if (!currentOrganizationId.value) {
      return null;
    }

    savingKeyId.value = keyId;
    error.value = null;

    try {
      const response = await LLMSettings.renameOrganizationLlmApiKey(
        currentOrganizationId.value,
        keyId,
        request,
      );
      const key = normalizeKey(response);
      await loadKeys();

      return key;
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      savingKeyId.value = null;
    }
  }

  /**
   * Replaces the encrypted bytes for an existing tenant-owned key.
   */
  async function rotateKey(keyId: string, request: RotateLlmApiKeyRequest) {
    if (!currentOrganizationId.value) {
      return null;
    }

    savingKeyId.value = keyId;
    error.value = null;

    try {
      const response = await LLMSettings.rotateOrganizationLlmApiKey(
        currentOrganizationId.value,
        keyId,
        request,
      );
      const key = normalizeKey(response);
      await loadKeys();

      return key;
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      savingKeyId.value = null;
    }
  }

  /**
   * Disables an unreferenced tenant-owned key.
   */
  async function deleteKey(keyId: string) {
    if (!currentOrganizationId.value) {
      return;
    }

    savingKeyId.value = keyId;
    error.value = null;

    try {
      await LLMSettings.deleteOrganizationLlmApiKey(
        currentOrganizationId.value,
        keyId,
      );
      await loadKeys();
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      savingKeyId.value = null;
    }
  }

  return {
    view,
    configuration,
    keys,
    loading,
    saving,
    savingKeyId,
    testingTier,
    testResults,
    error,
    currentOrganizationId,
    canManage,
    notice,
    loadLlmSettings,
    loadKeys,
    saveLlmSettings,
    testLlmConfiguration,
    createKey,
    renameKey,
    rotateKey,
    deleteKey,
  };

  /** Clears organization-scoped state when there is no active org context. */
  function clearSettings() {
    view.value = null;
    configuration.value = null;
    keys.value = [];
  }

  /** Applies a normalized API response to the store's flattened view state. */
  function setView(response: LlmSettingsView) {
    view.value = response;
    configuration.value = response.configuration;
    keys.value = response.keys;
  }
});

/** Builds the default tier configuration used before the org has saved one. */
export function defaultLlmConfiguration(): LlmSettingsConfiguration {
  return {
    fast: {
      provider: "Fireworks",
      model: llmModelDefaults.Fireworks.fast,
      keyId: null,
      endpoint: null,
    },
    high: {
      provider: "Fireworks",
      model: llmModelDefaults.Fireworks.high,
      keyId: null,
      endpoint: null,
    },
    max: {
      provider: "Fireworks",
      model: llmModelDefaults.Fireworks.max,
      keyId: null,
      endpoint: null,
    },
  };
}

/** Narrows arbitrary strings to the three supported tier identifiers. */
export function isLlmTierName(tier: string): tier is LlmTierName {
  return llmTierNames.some((candidate) => candidate === tier);
}

/** Narrows arbitrary strings to providers known by the first-phase UI catalog. */
export function isLlmProvider(provider: string): provider is LlmProvider {
  return Object.hasOwn(llmModelCatalog, provider);
}

/**
 * Per-provider capability flags driving UI rules and validation.
 * Add one entry here when wiring a new provider — all downstream checks derive from this map.
 */
export const providerCapabilities = {
  Fireworks: { requiresManagedKey: false, requiresEndpoint: false },
  OpenAI: { requiresManagedKey: true, requiresEndpoint: false },
  "Azure OpenAI": { requiresManagedKey: true, requiresEndpoint: true },
  Anthropic: { requiresManagedKey: true, requiresEndpoint: false },
} as const satisfies Record<
  LlmProvider,
  { requiresManagedKey: boolean; requiresEndpoint: boolean }
>;

/** Looks up capability flags for a provider string, falling back to safe defaults for unknown values. */
export function llmProviderCapabilities(provider: string): {
  requiresManagedKey: boolean;
  requiresEndpoint: boolean;
} {
  return isLlmProvider(provider)
    ? providerCapabilities[provider]
    : { requiresManagedKey: true, requiresEndpoint: false };
}

/** Produces the display label for encrypted key metadata. */
export function llmKeyDisplayName(key: LlmApiKey): string {
  if (key.name?.trim()) {
    return key.name.trim();
  }

  return `key_${key.id.slice(-6)}`;
}

/** Normalizes the settings endpoint's generated `unknown` response. */
function normalizeView(value: unknown): LlmSettingsView {
  if (!isRecord(value)) {
    return {
      canManage: false,
      notice: "This view requires admin or owner access.",
      configuration: null,
      keys: [],
    };
  }

  return {
    canManage: readBoolean(value.canManage),
    notice: readNullableString(value.notice),
    configuration: normalizeConfiguration(value.configuration),
    keys: normalizeKeys(value.keys),
  };
}

/** Reads the three tier settings from an API response object. */
function normalizeConfiguration(
  value: unknown,
): LlmSettingsConfiguration | null {
  if (!isRecord(value)) {
    return null;
  }

  return {
    fast: normalizeTier(value.fast, defaultLlmConfiguration().fast),
    high: normalizeTier(value.high, defaultLlmConfiguration().high),
    max: normalizeTier(value.max, defaultLlmConfiguration().max),
  };
}

/** Reads one tier object while falling back to product defaults. */
function normalizeTier(
  value: unknown,
  fallback: LlmTierSettings,
): LlmTierSettings {
  if (!isRecord(value)) {
    return { ...fallback };
  }

  const provider = readString(value.provider) || fallback.provider;
  const model = readString(value.model) || fallback.model;

  return {
    provider,
    model,
    keyId: readNullableString(value.keyId),
    endpoint: readNullableString(value.endpoint),
  };
}

/** Converts generated `unknown` key arrays into typed metadata. */
function normalizeKeys(value: unknown): LlmApiKey[] {
  if (!Array.isArray(value)) {
    return [];
  }

  const result: LlmApiKey[] = [];
  for (const item of value) {
    const key = normalizeKey(item);
    if (key) {
      result.push(key);
    }
  }

  return result;
}

/** Reads one encrypted key metadata object. */
function normalizeKey(value: unknown): LlmApiKey | null {
  if (!isRecord(value)) {
    return null;
  }

  const id = readString(value.id);
  if (!id) {
    return null;
  }

  return {
    id,
    name: readNullableString(value.name),
    createdAtUtc: readString(value.createdAtUtc),
    updatedAtUtc: readString(value.updatedAtUtc),
  };
}

/** Reads sanitized provider access test results from the API. */
function normalizeTestResult(value: unknown): LlmProviderAccessTestResult {
  if (!isRecord(value)) {
    return {
      success: false,
      provider: "",
      model: "",
      latencyMs: 0,
      errorCode: "invalid-response",
      message: "The provider test returned an unexpected response.",
    };
  }

  return {
    success: readBoolean(value.success),
    provider: readString(value.provider),
    model: readString(value.model),
    latencyMs: readNumber(value.latencyMs),
    errorCode: readNullableString(value.errorCode),
    message: readString(value.message) || "Provider test completed.",
  };
}

/** Narrows unknown API values before reading generated JSON fields. */
function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/** Reads string fields while avoiding unsafe casts. */
function readString(value: unknown): string {
  return typeof value === "string" ? value : "";
}

/** Reads optional string fields from API responses. */
function readNullableString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

/** Reads boolean fields with false as the stable fallback. */
function readBoolean(value: unknown): boolean {
  return typeof value === "boolean" ? value : false;
}

/** Reads numeric values that may arrive as either JSON numbers or strings. */
function readNumber(value: unknown): number {
  if (typeof value === "number") {
    return value;
  }

  if (typeof value !== "string") {
    return 0;
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : 0;
}

/** Normalizes thrown values for UI error surfaces. */
function toErrorMessage(err: unknown): string {
  return err instanceof Error ? err.message : "Unknown LLM settings error";
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useLlmSettingsStore, import.meta.hot));
}
