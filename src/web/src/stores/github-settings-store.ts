import { defineStore, acceptHMRUpdate } from "pinia";
import { GitHub } from "@/api/generated";
import { ZeeqApiError } from "@/api/zeeq-api-client";
import { useAppStore } from "@/stores/app-store";

/**
 * Repository returned from the GitHub App installation-visible list.
 *
 * Generated Kubb response types are currently `unknown` for these minimal API
 * endpoints, so this store owns the typed contract used by the settings UI.
 */
export type GitHubAvailableRepository = {
  gitHubRepositoryId: number;
  nodeId: string;
  name: string;
  ownerQualifiedName: string;
  private: boolean;
  defaultBranch: string;
  htmlUrl: string;
  configured: boolean;
  configuredRepositoryId: string | null;
  visibleInLibraryPicker: boolean;
};

/**
 * Local Zeeq repository mapping used to gate GitHub webhook processing.
 */
export type GitHubConfiguredRepository = {
  id: string;
  teamId: string | null;
  ownerQualifiedName: string;
  displayName: string;
  enabled: boolean;
  visibleInLibraryPicker: boolean;
  libraryIds: string[];
  createdAtUtc: string;
  updatedAtUtc: string;
};

/**
 * View model for one repository row in the GitHub settings page.
 */
export type GitHubRepositoryMappingRow = {
  gitHubRepositoryId: number;
  nodeId: string;
  name: string;
  ownerQualifiedName: string;
  private: boolean;
  defaultBranch: string;
  htmlUrl: string;
  configured: boolean;
  configuredRepositoryId: string | null;
  visibleInLibraryPicker: boolean;
  configuredMapping: GitHubConfiguredRepository | null;
};

/**
 * Store for the GitHub settings screen.
 *
 * It keeps installation-visible repositories and local mappings together
 * because the UI needs to show one row per GitHub repository while preserving
 * the backend distinction between configured, paused, and not configured.
 */
export const useGitHubSettingsStore = defineStore(
  "github-settings-store",
  () => {
    const appStore = useAppStore();
    const availableRepositories = ref<GitHubAvailableRepository[]>([]);
    const configuredRepositories = ref<GitHubConfiguredRepository[]>([]);
    const installationConnected = ref<boolean | null>(null);
    const loadingRepositories = ref(false);
    const savingRepositoryId = ref<string | null>(null);
    const error = ref<string | null>(null);

    const configuredById = computed(() => {
      const byId = new Map<string, GitHubConfiguredRepository>();

      for (const repository of configuredRepositories.value) {
        byId.set(repository.id, repository);
      }

      return byId;
    });

    const activeOrganizationId = computed(
      () => appStore.user?.organizationId ?? null,
    );

    /**
     * Rows merge GitHub's installation-visible list with Zeeq's configured
     * mappings. Paused mappings stay attached because the backend includes them
     * in the configured endpoint.
     */
    const repositoryRows = computed<GitHubRepositoryMappingRow[]>(() =>
      availableRepositories.value.map((repository) => {
        const configuredMapping = repository.configuredRepositoryId
          ? (configuredById.value.get(repository.configuredRepositoryId) ??
            null)
          : null;

        return {
          ...repository,
          visibleInLibraryPicker:
            configuredMapping?.visibleInLibraryPicker ??
            repository.visibleInLibraryPicker,
          configuredMapping,
        };
      }),
    );

    const librarySourceRepositories = computed(() =>
      repositoryRows.value.filter(
        (repository) => repository.visibleInLibraryPicker,
      ),
    );

    /**
     * Loads the repositories visible to the GitHub App and local Zeeq mappings.
     */
    async function loadRepositories() {
      loadingRepositories.value = true;
      error.value = null;

      try {
        const orgId = requireOrganizationId();
        const available = await GitHub.listAvailableGitHubRepositories(orgId);
        installationConnected.value = true;
        const configured = await GitHub.listConfiguredGitHubRepositories(orgId);
        availableRepositories.value = normalizeAvailableRepositories(available);
        configuredRepositories.value =
          normalizeConfiguredRepositories(configured);
      } catch (err: unknown) {
        if (err instanceof ZeeqApiError && err.status === 404) {
          installationConnected.value = false;
          availableRepositories.value = [];
          configuredRepositories.value = [];
        }

        error.value = toErrorMessage(err);
        throw err;
      } finally {
        loadingRepositories.value = false;
      }
    }

    /**
     * Registers a GitHub repository so webhook ingress can resolve it into the
     * active Zeeq organization.
     *
     * @param ownerQualifiedName - GitHub `owner/repository` name to register.
     */
    async function enableRepository(ownerQualifiedName: string) {
      savingRepositoryId.value = ownerQualifiedName;
      error.value = null;

      try {
        await GitHub.createGitHubRepositoryMapping(requireOrganizationId(), {
          ownerQualifiedName,
          teamId: null,
          displayName: null,
          enabled: true,
          visibleInLibraryPicker: true,
        });
        await loadRepositories();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        savingRepositoryId.value = null;
      }
    }

    /**
     * Updates the reversible enabled/paused state for an existing mapping.
     *
     * @param repository - Current mapping returned by the configured endpoint.
     * @param enabled - True to admit webhooks, false to pause queue ingress.
     */
    async function setRepositoryEnabled(
      repository: GitHubConfiguredRepository,
      enabled: boolean,
    ) {
      savingRepositoryId.value = repository.id;
      error.value = null;

      try {
        await GitHub.updateGitHubRepositoryMapping(
          requireOrganizationId(),
          repository.id,
          {
            teamId: repository.teamId,
            displayName: repository.displayName,
            enabled,
          },
        );
        await loadRepositories();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        savingRepositoryId.value = null;
      }
    }

    /**
     * Removes the active mapping row. A future enable creates a fresh mapping
     * while backend history remains attached to the old row.
     *
     * @param repositoryId - Local Zeeq repository mapping id.
     */
    async function removeRepository(repositoryId: string) {
      savingRepositoryId.value = repositoryId;
      error.value = null;

      try {
        await GitHub.disableGitHubRepositoryMapping(
          requireOrganizationId(),
          repositoryId,
        );
        await loadRepositories();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        savingRepositoryId.value = null;
      }
    }

    /**
     * Replaces the library mapping for a configured repository.
     *
     * Uses the GitHub update endpoint's three-way convention:
     * a non-empty `libraryIds` array replaces all existing mappings;
     * pass `[]` to clear all.
     *
     * @param repository - The configured repository to update.
     * @param libraryIds - Full set of library IDs to assign to this repository.
     */
    async function updateRepositoryLibraries(
      repository: GitHubConfiguredRepository,
      libraryIds: string[],
    ) {
      savingRepositoryId.value = repository.id;
      error.value = null;

      try {
        await GitHub.updateGitHubRepositoryMapping(
          requireOrganizationId(),
          repository.id,
          {
            teamId: repository.teamId,
            displayName: repository.displayName,
            enabled: repository.enabled,
            visibleInLibraryPicker: repository.visibleInLibraryPicker,
            libraryIds,
          },
        );
        await loadRepositories();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        savingRepositoryId.value = null;
      }
    }

    async function setRepositoryLibraryPickerVisible(
      ownerQualifiedName: string,
      visibleInLibraryPicker: boolean,
    ) {
      savingRepositoryId.value = ownerQualifiedName;
      error.value = null;

      try {
        await GitHub.updateGitHubRepositoryVisibility(requireOrganizationId(), {
          ownerQualifiedName,
          visibleInLibraryPicker,
        });
        await loadRepositories();
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        savingRepositoryId.value = null;
      }
    }

    async function ensureRepositoryForLibrarySource(
      ownerQualifiedName: string,
    ): Promise<GitHubConfiguredRepository> {
      const existing = configuredRepositories.value.find(
        (repository) =>
          repository.ownerQualifiedName.toLowerCase() ===
          ownerQualifiedName.toLowerCase(),
      );

      if (existing) {
        return existing;
      }

      savingRepositoryId.value = ownerQualifiedName;
      error.value = null;

      try {
        const created = await GitHub.createGitHubRepositoryMapping(
          requireOrganizationId(),
          {
            ownerQualifiedName,
            teamId: null,
            displayName: null,
            enabled: false,
            visibleInLibraryPicker: true,
          },
        );
        const repository = normalizeConfiguredRepository(created);
        if (!repository) {
          throw new Error(
            "GitHub repository response was missing repository details.",
          );
        }

        await loadRepositories();
        return repository;
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        savingRepositoryId.value = null;
      }
    }

    function requireOrganizationId(): string {
      if (!activeOrganizationId.value) {
        throw new Error(
          "Select an organization before managing GitHub repositories.",
        );
      }

      return activeOrganizationId.value;
    }

    return {
      availableRepositories,
      configuredRepositories,
      installationConnected,
      loadingRepositories,
      savingRepositoryId,
      error,
      repositoryRows,
      librarySourceRepositories,
      loadRepositories,
      enableRepository,
      setRepositoryEnabled,
      setRepositoryLibraryPickerVisible,
      ensureRepositoryForLibrarySource,
      removeRepository,
      updateRepositoryLibraries,
    };
  },
);

/** Converts generated `unknown` payloads into repository rows for rendering. */
function normalizeAvailableRepositories(
  value: unknown,
): GitHubAvailableRepository[] {
  if (!Array.isArray(value)) {
    return [];
  }

  const repositories: GitHubAvailableRepository[] = [];

  for (const item of value) {
    const repository = normalizeAvailableRepository(item);
    if (repository) {
      repositories.push(repository);
    }
  }

  return repositories;
}

/** Converts generated `unknown` payloads into configured mapping rows. */
function normalizeConfiguredRepositories(
  value: unknown,
): GitHubConfiguredRepository[] {
  if (!Array.isArray(value)) {
    return [];
  }

  const repositories: GitHubConfiguredRepository[] = [];

  for (const item of value) {
    const repository = normalizeConfiguredRepository(item);
    if (repository) {
      repositories.push(repository);
    }
  }

  return repositories;
}

/** Reads one installation-visible repository from an API response object. */
function normalizeAvailableRepository(
  value: unknown,
): GitHubAvailableRepository | null {
  if (!isRecord(value)) {
    return null;
  }

  const gitHubRepositoryId = readNumber(value.gitHubRepositoryId);
  const name = readString(value.name);
  const ownerQualifiedName = readString(value.ownerQualifiedName);

  if (gitHubRepositoryId === null || !name || !ownerQualifiedName) {
    return null;
  }

  return {
    gitHubRepositoryId,
    nodeId: readString(value.nodeId),
    name,
    ownerQualifiedName,
    private: readBoolean(value.private),
    defaultBranch: readString(value.defaultBranch),
    htmlUrl: readString(value.htmlUrl),
    configured: readBoolean(value.configured),
    configuredRepositoryId: readNullableString(value.configuredRepositoryId),
    visibleInLibraryPicker: readBoolean(value.visibleInLibraryPicker, true),
  };
}

/** Reads one configured Zeeq mapping from an API response object. */
function normalizeConfiguredRepository(
  value: unknown,
): GitHubConfiguredRepository | null {
  if (!isRecord(value)) {
    return null;
  }

  const id = readString(value.id);
  const ownerQualifiedName = readString(value.ownerQualifiedName);
  const displayName = readString(value.displayName);

  if (!id || !ownerQualifiedName || !displayName) {
    return null;
  }

  return {
    id,
    teamId: readNullableString(value.teamId),
    ownerQualifiedName,
    displayName,
    enabled: readBoolean(value.enabled),
    visibleInLibraryPicker: readBoolean(value.visibleInLibraryPicker, true),
    libraryIds: readStringArray(value.libraryIds),
    createdAtUtc: readString(value.createdAtUtc),
    updatedAtUtc: readString(value.updatedAtUtc),
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

/** Reads boolean fields with a caller-selected fallback. */
function readBoolean(value: unknown, fallback = false): boolean {
  return typeof value === "boolean" ? value : fallback;
}

/** Reads numeric identifiers that may arrive as either JSON numbers or strings. */
function readNumber(value: unknown): number | null {
  if (typeof value === "number") {
    return value;
  }

  if (typeof value !== "string") {
    return null;
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

/** Reads a string array field, returning [] for missing or non-array values. */
function readStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.filter((item): item is string => typeof item === "string");
}

/** Normalizes thrown values for UI error surfaces. */
function toErrorMessage(err: unknown): string {
  return err instanceof Error ? err.message : "Unknown GitHub settings error";
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useGitHubSettingsStore, import.meta.hot),
  );
}
