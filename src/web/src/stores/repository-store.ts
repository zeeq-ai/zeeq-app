import { defineStore, acceptHMRUpdate } from "pinia";
import { GitHub } from "@/api/generated";
import type { RepositoryPromptSummaryResponse } from "@/api/generated/types/RepositoryPromptSummaryResponse";
import type { RepositoryPromptDetailResponse } from "@/api/generated/types/RepositoryPromptDetailResponse";
import { useAppStore } from "@/stores/app-store";
import { useGitHubSettingsStore } from "@/stores/github-settings-store";
import { useLibraryStore } from "@/stores/library-store";

/** Tenant-scoped prompt detail key, mirroring organization_id/library_id/document_id. */
export function repositoryPromptKey(
  organizationId: string,
  libraryId: string,
  documentId: string,
): string {
  return `${organizationId}:${libraryId}:${documentId}`;
}

/**
 * Store for the Repositories view.
 *
 * Scope note: this store deliberately covers only the repository settings any
 * organization member may edit — library mapping and MCP prompt customization.
 * Connecting the GitHub App and enabling, pausing, or removing a repository stay
 * in `github-settings-store` behind the owner/admin surface, mirroring the
 * backend split between the management route group and the member-accessible one.
 *
 * The configured-repository list is reused from `github-settings-store` rather
 * than re-fetched, since that store already owns the normalized mapping rows.
 */
export const useRepositoryStore = defineStore("repository-store", () => {
  const appStore = useAppStore();
  const githubSettingsStore = useGitHubSettingsStore();
  const libraryStore = useLibraryStore();

  const selectedRepositoryId = ref<string | null>(null);
  const prompts = ref<RepositoryPromptSummaryResponse[]>([]);

  /**
   * Placeholder detail per tenant-scoped prompt key, loaded when a user expands
   * a row. Prompts are listed without their bodies, so this fills in on demand
   * rather than making the list a content read per prompt.
   */
  const promptDetails = ref<Record<string, RepositoryPromptDetailResponse>>({});

  const loadingRepositories = ref(false);
  const loadingPrompts = ref(false);
  const loadingPromptDetailId = ref<string | null>(null);
  const savingPromptId = ref<string | null>(null);
  const savingLibraries = ref(false);
  const error = ref<string | null>(null);

  const activeOrganizationId = computed(
    () => appStore.user?.organizationId ?? null,
  );

  /** Configured repository mappings, including paused ones. */
  const repositories = computed(
    () => githubSettingsStore.configuredRepositories,
  );

  /** Libraries available to map to a repository. */
  const libraries = computed(() => libraryStore.libraries);

  const selectedRepository = computed(
    () =>
      repositories.value.find(
        (repository) => repository.id === selectedRepositoryId.value,
      ) ?? null,
  );

  /**
   * Loads repository mappings and the library catalog the mapping editor needs.
   *
   * Uses the configured-only endpoint rather than the installation-visible list,
   * because the latter requires owner/admin and this view must work for members.
   */
  async function loadRepositories() {
    loadingRepositories.value = true;
    error.value = null;

    try {
      await githubSettingsStore.loadConfiguredRepositories();
      // Libraries load alongside but must not block the repository list.
      libraryStore.loadLibraryList().catch(() => undefined);
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      loadingRepositories.value = false;
    }
  }

  /**
   * Selects a repository and loads its prompt catalog.
   *
   * Cached placeholder detail is dropped because it is repository-specific: the
   * saved values shown for a prompt belong to the previously selected repository.
   */
  async function selectRepository(repositoryId: string | null) {
    selectedRepositoryId.value = repositoryId;
    prompts.value = [];
    promptDetails.value = {};

    if (repositoryId) {
      await loadPrompts();
    }
  }

  /**
   * Loads the organization prompt catalog with this repository's activation state.
   *
   * NOTE: A slower request for a previously selected repository can briefly win
   * this assignment after rapid navigation. We accept that low-risk UI race here
   * because repository selection clears this state and all writes remain scoped
   * by the current server-side organization/repository route.
   */
  async function loadPrompts() {
    if (!selectedRepositoryId.value) return;

    loadingPrompts.value = true;
    error.value = null;

    try {
      prompts.value = await GitHub.listRepositoryPrompts(
        requireOrganizationId(),
        selectedRepositoryId.value,
      );
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      loadingPrompts.value = false;
    }
  }

  /**
   * Loads one prompt's declared placeholders merged with saved values.
   *
   * NOTE: Detail cache keys mirror the persisted prompt identity. They do not
   * include repository_id, so a late response from a previous selection could
   * briefly show stale values for the same prompt. This is intentionally ignored
   * as low risk for now; server calls remain repository-scoped.
   *
   * @param documentId - Prompt document to expand.
   * @param libraryId - Library owning the document; part of its identity.
   */
  async function loadPromptDetail(documentId: string, libraryId: string) {
    if (!selectedRepositoryId.value) return;

    const key = repositoryPromptKey(
      requireOrganizationId(),
      libraryId,
      documentId,
    );
    loadingPromptDetailId.value = key;
    error.value = null;

    try {
      const detail = await GitHub.getRepositoryPrompt(
        requireOrganizationId(),
        selectedRepositoryId.value,
        documentId,
        { libraryId },
      );
      promptDetails.value = { ...promptDetails.value, [key]: detail };
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      loadingPromptDetailId.value = null;
    }
  }

  /**
   * Saves activation state and placeholder values for one prompt.
   *
   * Patches both the detail cache and the summary row in place so the accordion
   * badge and the expanded inputs cannot disagree after a save.
   */
  async function savePrompt(
    documentId: string,
    libraryId: string,
    active: boolean,
    values: Record<string, string>,
  ) {
    if (!selectedRepositoryId.value) return;

    const key = repositoryPromptKey(
      requireOrganizationId(),
      libraryId,
      documentId,
    );
    savingPromptId.value = key;
    error.value = null;

    try {
      const detail = await GitHub.saveRepositoryPrompt(
        requireOrganizationId(),
        selectedRepositoryId.value,
        documentId,
        { libraryId, active, values },
      );
      promptDetails.value = { ...promptDetails.value, [key]: detail };
      prompts.value = prompts.value.map((prompt) =>
        prompt.documentId === documentId && prompt.libraryId === libraryId
          ? {
              ...prompt,
              active: detail.active,
              configuredValueCount: detail.placeholders.filter(
                (placeholder) => placeholder.value !== null,
              ).length,
            }
          : prompt,
      );
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      savingPromptId.value = null;
    }
  }

  /**
   * Replaces the repository's mapped libraries.
   *
   * Calls the library-only endpoint rather than the general repository update,
   * so this works for members and cannot alter enabled/display/team settings.
   */
  async function saveLibraries(libraryIds: string[]) {
    if (!selectedRepositoryId.value) return;

    savingLibraries.value = true;
    error.value = null;

    try {
      await GitHub.updateRepositoryLibraries(
        requireOrganizationId(),
        selectedRepositoryId.value,
        { libraryIds },
      );
      // Refresh mappings so the checkbox state reflects what was persisted.
      await githubSettingsStore.loadConfiguredRepositories();
    } catch (err: unknown) {
      error.value = toErrorMessage(err);
      throw err;
    } finally {
      savingLibraries.value = false;
    }
  }

  function requireOrganizationId(): string {
    if (!activeOrganizationId.value) {
      throw new Error(
        "Select an organization before configuring repositories.",
      );
    }

    return activeOrganizationId.value;
  }

  return {
    selectedRepositoryId,
    selectedRepository,
    repositories,
    libraries,
    prompts,
    promptDetails,
    loadingRepositories,
    loadingPrompts,
    loadingPromptDetailId,
    savingPromptId,
    savingLibraries,
    error,
    activeOrganizationId,
    loadRepositories,
    selectRepository,
    loadPrompts,
    loadPromptDetail,
    savePrompt,
    saveLibraries,
  };
});

/** Normalizes thrown values for UI error surfaces. */
function toErrorMessage(err: unknown): string {
  return err instanceof Error
    ? err.message
    : "Unknown repository settings error";
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useRepositoryStore, import.meta.hot));
}
