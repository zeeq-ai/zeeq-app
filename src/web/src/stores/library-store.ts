import { defineStore, acceptHMRUpdate } from "pinia";
import { useLocalStorage } from "@vueuse/core";
import {
  Libraries,
  LibraryDocuments,
  LibrarySnippets,
  Ingest,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import type { LibraryResponse } from "@/api/generated/types/LibraryResponse";
import type { DocumentResponse } from "@/api/generated/types/DocumentResponse";
import type { DocumentContentResponse } from "@/api/generated/types/DocumentContentResponse";
import type { DocumentSearchResultResponse } from "@/api/generated/types/DocumentSearchResultResponse";
import type { SnippetSearchResultResponse } from "@/api/generated/types/SnippetSearchResultResponse";
import type { DocumentParsePreviewResponse } from "@/api/generated/types/DocumentParsePreviewResponse";
import type { CreateLibraryRequest } from "@/api/generated/types/CreateLibraryRequest";
import type { CreateLibrarySourceRequest } from "@/api/generated/types/CreateLibrarySourceRequest";
import type { UpdateLibraryRequest } from "@/api/generated/types/UpdateLibraryRequest";
import type { UpdateLibraryRepositoryMappingsRequest } from "@/api/generated/types/UpdateLibraryRepositoryMappingsRequest";
import type { UpsertDocumentRequest } from "@/api/generated/types/UpsertDocumentRequest";
import type { RenameDocumentRequest } from "@/api/generated/types/RenameDocumentRequest";
import type { SetDocumentReviewExclusionRequest } from "@/api/generated/types/SetDocumentReviewExclusionRequest";
import type { TriggerIngestRunResponse } from "@/api/generated/types/TriggerIngestRunResponse";
import type { IngestRunPageResponse } from "@/api/generated/types/IngestRunPageResponse";

/**
 * Library management store: libraries (CRUD), the documents in the active library,
 * the loaded document body for editing, and keyword "Test" search results.
 *
 * Only the root `Libraries.vue` consumes this store directly; children take props
 * and emit events. Active organization is implicit in the auth cookie — the API
 * derives it from claims.
 */
export const useLibraryStore = defineStore("library", () => {
  const appStore = useAppStore();
  const orgId = computed(() => appStore.user?.organizationId ?? "");

  // ── State ──────────────────────────────────────────────────────────────

  const libraries = ref<LibraryResponse[]>([]);

  /** Persisted active library name across sessions (preference only). */
  const activeLibraryName = useLocalStorage<string | null>(
    "zeeq:active-library",
    null,
  );

  /** Document summaries for the tree (no body content). */
  const documents = ref<DocumentResponse[]>([]);

  /** Full document body loaded for the editor. */
  const loadedDocument = ref<DocumentContentResponse | null>(null);

  /** Ranked search results from the "Test" panel. */
  const searchResults = ref<DocumentSearchResultResponse[]>([]);

  /**
   * Ranked snippet (Sections/Code mode) results from the "Test" panel, scoped by kind.
   * Kind-scoped rather than one shared field so switching between Sections and Code without
   * re-searching can never show stale rows from the other kind under the wrong mode's
   * rendering — the freshness guarantee is structural, not tracked separately (code review
   * follow-up, 2026-07-11).
   */
  const snippetSearchResultsByKind = reactive<{
    section: SnippetSearchResultResponse[];
    code: SnippetSearchResultResponse[];
  }>({
    section: [],
    code: [],
  });

  /** Result of the most recent "Preview parse" call for the parse-preview slideover. */
  const parsePreview = ref<DocumentParsePreviewResponse | null>(null);

  const loadingLibraries = ref(false);
  const loadingDocuments = ref(false);
  const loadingDocument = ref(false);
  const searching = ref(false);
  const snippetSearching = ref(false);
  const loadingParsePreview = ref(false);

  // ── Computed ───────────────────────────────────────────────────────────

  /** The currently selected library entity, or null when none is active. */
  const activeLibrary = computed(
    () =>
      libraries.value.find((l) => l.name === activeLibraryName.value) ?? null,
  );

  /** Flat list of document paths for editor completions and tree building. */
  const documentPaths = computed(() => documents.value.map((d) => d.path));

  // ── Actions ────────────────────────────────────────────────────────────

  /**
   * Loads all libraries in the active organization and picks the active one.
   * Falls back to the first library when the persisted name is gone.
   */
  async function loadLibraries() {
    loadingLibraries.value = true;
    try {
      libraries.value = await Libraries.listLibraries(orgId.value);

      if (activeLibraryName.value && !activeLibrary.value) {
        // Persisted name is stale (its library was deleted/renamed elsewhere,
        // e.g. by deleteLibrary) — fall back to first library and reload its
        // documents; otherwise the tree keeps showing the old library's
        // (now-irrelevant) document list.
        activeLibraryName.value = libraries.value[0]?.name ?? null;
        loadedDocument.value = null;
        searchResults.value = [];
        snippetSearchResultsByKind.section = [];
        snippetSearchResultsByKind.code = [];
        await loadDocuments();
      }

      if (!activeLibraryName.value && libraries.value.length > 0) {
        // Initial mount, no persisted name yet — the caller (Libraries.vue's
        // onMounted) already loads documents for this case itself, so this
        // branch only needs to pick the name.
        activeLibraryName.value = libraries.value[0].name;
      }
    } finally {
      loadingLibraries.value = false;
    }
  }

  /**
   * Re-fetches one library by name and patches it into `libraries` in place.
   * Used for status polling (sync status/next-run-time) without reloading
   * the whole list. Returns the refreshed library.
   */
  async function refreshLibrary(name: string): Promise<LibraryResponse> {
    const refreshed = (await Libraries.getLibrary(
      orgId.value,
      name,
    )) as LibraryResponse;
    const index = libraries.value.findIndex((l) => l.name === name);
    if (index >= 0) {
      libraries.value[index] = refreshed;
    }

    return refreshed;
  }

  /**
   * Creates a library and refreshes the list. Returns the created library.
   * Pass `source` to create a repository-sourced library instead of a plain
   * hand-authored one. Does not queue the initial sync — call
   * `triggerIngest(name)` immediately after for a repository-sourced library.
   */
  async function createLibrary(
    name: string,
    description?: string,
    source?: CreateLibrarySourceRequest,
  ): Promise<LibraryResponse> {
    const request: CreateLibraryRequest = { name };
    if (description) {
      request.description = description;
    }
    if (source) {
      request.source = source;
    }

    const created = await Libraries.createLibrary(orgId.value, request);
    await loadLibraries();
    // Auto-select the newly created library and load its (empty) document
    // list — without this, the tree keeps showing whatever was previously
    // active until something else happens to trigger a reload.
    activeLibraryName.value = name;
    loadedDocument.value = null;
    searchResults.value = [];
    snippetSearchResultsByKind.section = [];
    snippetSearchResultsByKind.code = [];
    await loadDocuments();

    return created as LibraryResponse;
  }

  /**
   * Updates an existing library's name, description, and (for a
   * repository-sourced library) include/exclude filters. Never changes the
   * repository URL/kind — delete and recreate to change the source. Returns
   * the updated library.
   */
  async function updateLibrary(
    currentName: string,
    next: {
      name: string;
      description?: string;
      includeFilters?: string[];
      excludeFilters?: string[];
    },
  ): Promise<LibraryResponse> {
    const request: UpdateLibraryRequest = {
      name: next.name,
      description: next.description,
      includeFilters: next.includeFilters,
      excludeFilters: next.excludeFilters,
    };

    const updated = await Libraries.updateLibrary(
      orgId.value,
      currentName,
      request,
    );
    await loadLibraries();
    activeLibraryName.value = next.name;

    return updated as LibraryResponse;
  }

  /**
   * Permanently deletes a library and all its documents (for a public-source
   * library, this only unsubscribes — the shared document set remains for
   * other orgs). `loadLibraries`'s own stale-selection fallback picks a new
   * active library if the deleted one was active.
   */
  async function deleteLibrary(name: string) {
    await Libraries.deleteLibrary(orgId.value, name);
    await loadLibraries();
  }

  /**
   * Queues an immediate sync for a repository-sourced library — the
   * "queue immediately on create" follow-up call, and the same path "Sync
   * now" uses. Works for both public- and private-source libraries (the
   * endpoint branches server-side). Throws on 409 (already in flight)/429
   * (rate limited) for the caller to surface as a toast.
   */
  async function triggerIngest(
    libraryName: string,
  ): Promise<TriggerIngestRunResponse> {
    return (await Ingest.triggerLibraryIngest(
      orgId.value,
      libraryName,
    )) as TriggerIngestRunResponse;
  }

  /**
   * Lists one page of ingest run history for a repository-sourced library,
   * newest first. Pass the previous page's `nextCursor` to fetch the next
   * page.
   */
  async function listIngestRuns(
    libraryName: string,
    cursor?: string,
    limit = 10,
  ): Promise<IngestRunPageResponse> {
    return (await Ingest.listLibraryIngestRuns(orgId.value, libraryName, {
      cursor,
      limit,
    })) as IngestRunPageResponse;
  }

  /**
   * Replaces the full set of repositories mapped to a library.
   *
   * @param libraryName - URL-segment name of the library.
   * @param repositoryIds - Full replacement set; pass `[]` to clear all mappings.
   */
  async function updateLibraryRepositories(
    libraryName: string,
    repositoryIds: string[],
  ) {
    const request: UpdateLibraryRepositoryMappingsRequest = { repositoryIds };
    await Libraries.updateLibraryRepositoryMappings(
      orgId.value,
      libraryName,
      request,
    );
  }

  /** Selects a library and loads its documents. */
  async function selectLibrary(name: string) {
    activeLibraryName.value = name;
    loadedDocument.value = null;
    searchResults.value = [];
    snippetSearchResultsByKind.section = [];
    snippetSearchResultsByKind.code = [];
    await loadDocuments();
  }

  /** Loads document summaries for the active library. */
  async function loadDocuments() {
    if (!activeLibraryName.value) {
      documents.value = [];
      return;
    }

    loadingDocuments.value = true;
    try {
      documents.value = await LibraryDocuments.listLibraryDocuments(
        orgId.value,
        activeLibraryName.value,
      );
    } finally {
      loadingDocuments.value = false;
    }
  }

  /**
   * Loads a document's full content by path for the editor.
   * Origin gates read-only at the view layer.
   */
  async function openDocument(path: string) {
    if (!activeLibraryName.value) {
      return;
    }

    loadingDocument.value = true;
    try {
      loadedDocument.value = await LibraryDocuments.getLibraryDocumentContent(
        orgId.value,
        activeLibraryName.value,
        { path },
      );
    } finally {
      loadingDocument.value = false;
    }
  }

  /** Clears the loaded document so the editor shows a new-doc template. */
  function newDocument() {
    loadedDocument.value = null;
  }

  /**
   * Saves (upserts) a document after diff review.
   * Path may be folder-prefixed; empty string ⇒ root.
   */
  async function saveDocument(path: string, content: string) {
    if (!activeLibraryName.value) {
      return;
    }

    const request: UpsertDocumentRequest = { path, content };
    await LibraryDocuments.upsertLibraryDocument(
      orgId.value,
      activeLibraryName.value,
      request,
    );
    await loadDocuments();
  }

  /** Deletes a document by path and refreshes the list. */
  async function deleteDocument(path: string) {
    if (!activeLibraryName.value) {
      return;
    }

    await LibraryDocuments.deleteLibraryDocument(
      orgId.value,
      activeLibraryName.value,
      {
        path,
      },
    );
    await loadDocuments();
  }

  /**
   * Renames (moves) a document to a new path (D-3).
   * Surfaces 409 (target taken) to the caller for toast display.
   */
  async function renameDocument(fromPath: string, toPath: string) {
    if (!activeLibraryName.value) {
      return;
    }

    const request: RenameDocumentRequest = {
      fromPath,
      toPath,
    };

    await LibraryDocuments.renameLibraryDocument(
      orgId.value,
      activeLibraryName.value,
      request,
    );
    await loadDocuments();
  }

  /**
   * Sets or clears a document's code-review exclusion flag. Excluded documents never
   * surface to code-review agents via list/search tools (direct reads by path still
   * resolve); all other callers are unaffected. Local (hand-authored) documents only —
   * the API rejects synced/remote documents with a 400.
   *
   * Refreshes the tree summaries (badge state) and the loaded document (toggle state)
   * so the UI reflects the change immediately.
   */
  async function setReviewExclusion(documentId: string, excluded: boolean) {
    if (!activeLibraryName.value) {
      return;
    }

    const request: SetDocumentReviewExclusionRequest = { documentId, excluded };
    await LibraryDocuments.setLibraryDocumentReviewExclusion(
      orgId.value,
      activeLibraryName.value,
      request,
    );
    await loadDocuments();

    if (loadedDocument.value?.id === documentId) {
      await openDocument(loadedDocument.value.path);
    }
  }

  /** Runs a keyword search in the active library for the "Test" panel. */
  async function testSearch(query: string, limit = 10) {
    if (!activeLibraryName.value) {
      return;
    }

    searching.value = true;
    try {
      searchResults.value = await LibraryDocuments.searchLibraryDocuments(
        orgId.value,
        activeLibraryName.value,
        { query, limit },
      );
    } finally {
      searching.value = false;
    }
  }

  /**
   * Runs a hybrid section/code snippet search in the active library for the "Test" panel's
   * Sections/Code modes. Always scoped to the active library (SnippetSearchService's
   * single-required-library contract) — there is no "all libraries" mode.
   */
  async function testSnippetSearch(
    query: string,
    kind: "section" | "code",
    excludeDocumentPaths?: string[],
    maxResults?: number,
  ) {
    if (!activeLibraryName.value) {
      return;
    }

    snippetSearching.value = true;
    try {
      snippetSearchResultsByKind[kind] =
        await LibrarySnippets.searchLibrarySnippets(
          orgId.value,
          activeLibraryName.value,
          { kind, query, excludeDocumentPaths, maxResults },
        );
    } finally {
      snippetSearching.value = false;
    }
  }

  /**
   * Previews what the parse/snippet-indexing pipeline would extract from a document's current
   * (persisted) content for the "Preview parse" slideover — title, keywords, headings, and the
   * section/code snippets it would compose. Read-only; nothing is written.
   *
   * Guarded by a request id (same pattern as `fetchUser` in app-store.ts): clicking "Preview
   * parse" on one document and then another before the first response lands must not let the
   * slower, now-stale response overwrite the newer one's results.
   */
  let previewDocumentParseRequestId = 0;

  async function previewDocumentParse(path: string) {
    if (!activeLibraryName.value) {
      return;
    }

    const requestId = ++previewDocumentParseRequestId;
    loadingParsePreview.value = true;
    try {
      const result = await LibraryDocuments.previewLibraryDocumentParse(
        orgId.value,
        activeLibraryName.value,
        { path },
      );

      if (requestId === previewDocumentParseRequestId) {
        parsePreview.value = result;
      }
    } finally {
      if (requestId === previewDocumentParseRequestId) {
        loadingParsePreview.value = false;
      }
    }
  }

  /** Clears the loaded parse preview, e.g. when the slideover closes. */
  function clearParsePreview() {
    parsePreview.value = null;
  }

  // ── Return ─────────────────────────────────────────────────────────────

  return {
    // State
    libraries,
    activeLibraryName,
    documents,
    loadedDocument,
    searchResults,
    snippetSearchResultsByKind,
    parsePreview,
    loadingLibraries,
    loadingDocuments,
    loadingDocument,
    searching,
    snippetSearching,
    loadingParsePreview,
    // Computed
    activeLibrary,
    documentPaths,
    // Actions
    loadLibraries,
    refreshLibrary,
    createLibrary,
    updateLibrary,
    deleteLibrary,
    triggerIngest,
    listIngestRuns,
    updateLibraryRepositories,
    selectLibrary,
    loadDocuments,
    openDocument,
    newDocument,
    saveDocument,
    deleteDocument,
    renameDocument,
    setReviewExclusion,
    testSearch,
    testSnippetSearch,
    previewDocumentParse,
    clearParsePreview,
  };
});

// Enable HMR for the store during development.
if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useLibraryStore, import.meta.hot));
}
