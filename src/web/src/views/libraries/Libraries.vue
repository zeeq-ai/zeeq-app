<template>
  <!--
  Root library management view: toolbar holds the library selector;
  body is a two-column split of tree (left) + editor (right).
  Edge-to-edge layout like code-reviews view.
  Only this component consumes the Pinia store; children take props and emit.
  -->
  <ZeeqView
    id="libraries"
    title="Libraries"
    body-class="gap-0 sm:gap-0 overflow-hidden p-0 sm:p-0"
  >
    <template #toolbar>
      <LibrarySelector
        :libraries="libraries"
        :active-library-name="activeLibraryName"
        :loading="loadingLibraries"
        :show-test="!!activeLibraryName && documents.length > 0"
        @select="onSelectLibrary"
        @add="openLibraryForm(null)"
        @edit="openLibraryForm"
        @test="openSearch"
      />
    </template>

    <!-- Two-column split: tree (left) + editor (right), edge-to-edge -->
    <div class="flex h-full min-h-0">
      <!-- Document tree sidebar -->
      <DocumentTree
        v-if="activeLibraryName"
        class="w-[368px] shrink-0 overflow-y-auto border-r border-default"
        :documents="documents"
        :loading="loadingDocuments"
        :has-library="!!activeLibraryName"
        :active-path="selectedFolderPath ?? loadedDocument?.path ?? null"
        @select="onOpenDocument"
        @folder-select="onSelectFolder"
        @add="onAddDocumentAt"
        @rename="onRenameDocument"
        @delete="onDeleteDocument"
        @refresh="onRefreshDocuments"
      />

      <!-- Editor panel (fills remaining space) -->
      <DocumentEditorPanel
        class="min-w-0 flex-1"
        :document="loadedDocument"
        :loading="editorLoading"
        :paths="documentPaths"
        :selected-folder-path="selectedFolderPath"
        :initial-folder-path="pendingNewDocumentFolder"
        :repo-url="activeLibraryRepoUrl"
        @create-library="openLibraryForm(null)"
        @delete="onDeleteDocument"
        @select-document="onOpenDocument"
        @select-folder="onSelectFolder"
        @review="openDiff"
        @preview-parse="onPreviewParse"
        @toggle-review-exclusion="onToggleReviewExclusion"
      />
    </div>

    <!-- Create / rename a library -->
    <LibraryFormSlideover
      v-model:open="libraryFormOpen"
      :library="libraryFormTarget"
      :repositories="configuredRepositories"
      :source-repositories="librarySourceRepositories"
      :mapped-repository-ids="libraryFormMappedRepoIds"
      :ingest-runs="ingestRunsPage"
      :loading-ingest-runs="loadingIngestRuns"
      :syncing="syncingLibrary"
      :resetting="resettingLibrarySync"
      :deleting="deletingLibrary"
      :submit-handler="onSubmitLibrary"
      @sync-now="onSyncNow"
      @reset-run-state="onResetRunState"
      @load-more-runs="onLoadMoreRuns"
      @imported="onLibraryImportComplete"
      @delete="onDeleteLibrary"
    />

    <DocumentRenameSlideover
      v-model:open="renameOpen"
      :from-path="renameFromPath"
      :submit-handler="onSubmitRename"
    />

    <!-- Reviewed save: side-by-side diff in a bottom drawer (D-6) -->
    <DocumentDiffDrawer
      v-model:open="diffOpen"
      :original="diffOriginal"
      :next="diffNext"
      @confirm="onConfirmSave"
    />

    <!-- "Test" search: Documents (D-1: each row carries its own library name),
    Sections, and Code modes -->
    <DocumentSearchPanel
      v-model:open="searchOpen"
      :results="searchResults"
      :searching="searching"
      :snippet-results-by-kind="snippetSearchResultsByKind"
      :snippet-searching="snippetSearching"
      @search="onTestSearch"
      @snippet-search="onTestSnippetSearch"
    />

    <!-- "Preview parse": title/keywords/headings/snippets for one document -->
    <DocumentParsePreviewSlideover
      v-model:open="parsePreviewOpen"
      :preview="parsePreview"
      :loading="loadingParsePreview"
    />
  </ZeeqView>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useIntervalFn } from "@vueuse/core";
import { useLibraryStore } from "@/stores/library-store";
import { useGitHubSettingsStore } from "@/stores/github-settings-store";
import type { LibraryResponse } from "@/api/generated/types/LibraryResponse";
import type { IngestRunPageResponse } from "@/api/generated/types/IngestRunPageResponse";
import LibrarySelector from "./LibrarySelector.vue";
import LibraryFormSlideover from "./LibraryFormSlideover.vue";
import DocumentTree from "./DocumentTree.vue";
import DocumentEditorPanel from "./DocumentEditorPanel.vue";
import DocumentDiffDrawer from "./DocumentDiffDrawer.vue";
import DocumentSearchPanel from "./DocumentSearchPanel.vue";
import DocumentParsePreviewSlideover from "./DocumentParsePreviewSlideover.vue";
import DocumentRenameSlideover from "./DocumentRenameSlideover.vue";

const toast = useToast();
const store = useLibraryStore();
const githubStore = useGitHubSettingsStore();
const route = useRoute();
const router = useRouter();

const {
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
  documentPaths,
} = storeToRefs(store);

const { configuredRepositories, librarySourceRepositories } =
  storeToRefs(githubStore);

const librarySelectionLoading = ref(false);
const editorLoading = computed(
  () =>
    librarySelectionLoading.value ||
    loadingLibraries.value ||
    loadingDocuments.value ||
    loadingDocument.value,
);

// ── Library form state ──────────────────────────────────────────────────

const libraryFormOpen = ref(false);
const libraryFormTarget = ref<LibraryResponse | null>(null);
/** Set on a successful create; forces a documents reload when the slideover
 * next closes, regardless of how it closes (Cancel, X, or after a rename/
 * sync/delete in the same session) — a safety net on top of createLibrary's
 * own immediate reload. */
const libraryJustCreated = ref(false);

/**
 * Repository IDs currently mapped to the library being edited.
 * Used to pre-seed the checkbox group in the form.
 */
const libraryFormMappedRepoIds = computed(() => {
  const libraryId = libraryFormTarget.value?.id;
  if (!libraryId) return [];

  return configuredRepositories.value
    .filter((r) => r.libraryIds.includes(libraryId))
    .map((r) => r.id);
});

/** Origin repo clone URL for the active library, if repository-driven. */
const activeLibraryRepoUrl = computed(
  () =>
    libraries.value.find((library) => library.name === activeLibraryName.value)
      ?.source?.repoUrl ?? null,
);

/** Opens the library create/edit slideover. Pass null for create mode. */
function openLibraryForm(library: LibraryResponse | null) {
  libraryFormTarget.value = library;
  ingestRunsPage.value = null;
  libraryJustCreated.value = false;
  libraryFormOpen.value = true;

  if (library?.source) {
    void loadIngestRunsFirstPage(library.name);
  }
}

/** Handles submit from the library form (create or update). */
async function onSubmitLibrary(data: {
  name: string;
  description?: string;
  repositoryIds: string[];
  source?: {
    kind: "Public" | "Private";
    repoUrl?: string;
    repositoryId?: string;
    ownerQualifiedName?: string;
    includeFilters: string[];
    excludeFilters: string[];
  };
  includeFilters?: string[];
  excludeFilters?: string[];
}) {
  try {
    if (libraryFormTarget.value) {
      const updated = await store.updateLibrary(libraryFormTarget.value.name, {
        name: data.name,
        description: data.description,
        includeFilters: data.includeFilters,
        excludeFilters: data.excludeFilters,
      });
      await store.updateLibraryRepositories(updated.name, data.repositoryIds);
      libraryFormTarget.value = updated;
      await router.replace(libraryRoute(updated.name));
      toast.add({ title: "Library updated", color: "success" });
    } else {
      const source = await resolveCreateSource(data.source);
      const created = await store.createLibrary(
        data.name,
        data.description,
        source,
      );
      await store.updateLibraryRepositories(created.name, data.repositoryIds);
      libraryFormTarget.value = created;
      libraryJustCreated.value = true;
      await router.push(libraryRoute(created.name));
      toast.add({ title: "Library created", color: "success" });

      if (created.source) {
        // "Queue immediately" per spec — a follow-up call to the same
        // trigger endpoint the "Sync now" button uses. A 409/429 here
        // doesn't fail the creation; it just means the initial sync will
        // run on the next scheduled cycle instead.
        //
        // NOTE: this trigger call carries no filter data of its own — the
        // form's include/exclude filters were already persisted onto the
        // Library row by createLibrary's POST, and the async sync handler
        // re-reads library.IncludeFilters/ExcludeFilters fresh from Postgres
        // when it processes the queued message (PrivateRepositorySyncRequestedHandler
        // / PublicRepositorySyncRequestedHandler), not from this client call.
        await onSyncNow({ silentOnRateLimit: true });
      }
    }
  } catch (err: any) {
    toast.add({
      title: "Error",
      description: err?.message ?? "Failed to save library",
      color: "error",
    });
  }
}

type CreateLibrarySourceInput = {
  kind: "Public" | "Private";
  repoUrl?: string;
  repositoryId?: string;
  ownerQualifiedName?: string;
  includeFilters: string[];
  excludeFilters: string[];
};

async function resolveCreateSource(
  dataSource: CreateLibrarySourceInput | undefined,
) {
  if (dataSource?.kind !== "Private" || dataSource.repositoryId) {
    return dataSource;
  }

  if (!dataSource.ownerQualifiedName) {
    return dataSource;
  }

  const repository = await githubStore.ensureRepositoryForLibrarySource(
    dataSource.ownerQualifiedName,
  );

  return {
    kind: "Private" as const,
    repositoryId: repository.id,
    includeFilters: dataSource.includeFilters,
    excludeFilters: dataSource.excludeFilters,
  };
}

// ── Sync status tab: trigger + run history + polling ────────────────────

const ingestRunsPage = ref<IngestRunPageResponse | null>(null);
const loadingIngestRuns = ref(false);
const syncingLibrary = ref(false);
const resettingLibrarySync = ref(false);
const deletingLibrary = ref(false);

/**
 * Polls the library's sync status every 3s while queued/running, pausing
 * once it settles. `useIntervalFn` owns the interval handle and its cleanup
 * (including on unmount), so there's a single lifecycle entry point instead
 * of a hand-rolled start/stop pair.
 */
const { pause: pausePolling, resume: resumePolling } = useIntervalFn(
  async () => {
    const name = libraryFormTarget.value?.name;
    const status = libraryFormTarget.value?.source?.syncStatus;
    if (!name || (status !== "queued" && status !== "running")) {
      pausePolling();
      return;
    }

    const refreshed = await store.refreshLibrary(name);
    libraryFormTarget.value = refreshed;

    const stillInFlight =
      refreshed.source?.syncStatus === "queued" ||
      refreshed.source?.syncStatus === "running";
    if (!stillInFlight) {
      await loadIngestRunsFirstPage(name);
      pausePolling();
    }
  },
  3000,
  { immediate: false },
);

async function loadIngestRunsFirstPage(name: string) {
  loadingIngestRuns.value = true;
  try {
    ingestRunsPage.value = await store.listIngestRuns(name);
  } finally {
    loadingIngestRuns.value = false;
  }
}

async function onLoadMoreRuns() {
  const name = libraryFormTarget.value?.name;
  const cursor = ingestRunsPage.value?.nextCursor;
  if (!name || !cursor) return;

  loadingIngestRuns.value = true;
  try {
    const nextPage = await store.listIngestRuns(name, cursor);
    ingestRunsPage.value = {
      runs: [...(ingestRunsPage.value?.runs ?? []), ...nextPage.runs],
      nextCursor: nextPage.nextCursor,
    };
  } finally {
    loadingIngestRuns.value = false;
  }
}

async function onLibraryImportComplete() {
  try {
    await store.loadDocuments();
    selectedFolderPath.value = "/";
  } catch (err: any) {
    toast.add({
      title: "Error refreshing documents",
      description: err?.message ?? "Failed to refresh imported documents",
      color: "error",
    });
  }
}

/** Triggers an immediate sync via the same endpoint used for "queue immediately" on create. */
async function onSyncNow(options?: { silentOnRateLimit?: boolean }) {
  const name = libraryFormTarget.value?.name;
  if (!name) return;

  syncingLibrary.value = true;
  try {
    await store.triggerIngest(name);
    libraryFormTarget.value = await store.refreshLibrary(name);
    await loadIngestRunsFirstPage(name);
    resumePolling();
  } catch (err: any) {
    const status = err?.status;
    if (options?.silentOnRateLimit && (status === 409 || status === 429)) {
      toast.add({
        title: "Library created",
        description:
          "The initial sync is rate-limited and will run on the next scheduled cycle.",
        color: "warning",
      });
      return;
    }

    toast.add({
      title: "Sync error",
      description: err?.message ?? "Failed to queue sync",
      color: "error",
    });
  } finally {
    syncingLibrary.value = false;
  }
}

/** Clears a stuck private-library sync state and refreshes the status panel. */
async function onResetRunState() {
  const name = libraryFormTarget.value?.name;
  if (!name) return;

  resettingLibrarySync.value = true;
  try {
    await store.resetIngestRunState(name);
    libraryFormTarget.value = await store.refreshLibrary(name);
    await loadIngestRunsFirstPage(name);
    pausePolling();
    toast.add({ title: "Sync state cleared", color: "success" });
  } catch (err: any) {
    toast.add({
      title: "Reset error",
      description: err?.message ?? "Failed to clear sync state",
      color: "error",
    });
  } finally {
    resettingLibrarySync.value = false;
  }
}

/** Deletes the library being edited (or unsubscribes, for a public source) and closes the slideover. */
async function onDeleteLibrary(name: string) {
  deletingLibrary.value = true;
  try {
    await store.deleteLibrary(name);
    await replaceRouteWithActiveLibrary();
    toast.add({ title: "Library deleted", color: "success" });
    pausePolling();
    libraryFormOpen.value = false;
    libraryFormTarget.value = null;
  } catch (err: any) {
    toast.add({
      title: "Error deleting library",
      description: err?.message ?? "Failed to delete library",
      color: "error",
    });
  } finally {
    deletingLibrary.value = false;
  }
}

watch(libraryFormOpen, (isOpen) => {
  if (!isOpen) {
    pausePolling();

    if (libraryJustCreated.value) {
      libraryJustCreated.value = false;
      void store.loadDocuments();
    }
  } else if (
    libraryFormTarget.value?.source?.syncStatus === "queued" ||
    libraryFormTarget.value?.source?.syncStatus === "running"
  ) {
    resumePolling();
  }
});

// ── Library selection ───────────────────────────────────────────────────

async function onSelectLibrary(name: string) {
  if (routeLibraryName() === name) {
    await selectLibraryAndRoot(name);
    return;
  }

  await router.push(libraryRoute(name));
}

// ── Document tree actions ───────────────────────────────────────────────

/** Manual reload of the active library's document list. */
async function onRefreshDocuments() {
  try {
    await store.loadDocuments();
  } catch (err: any) {
    toast.add({
      title: "Error refreshing documents",
      description: err?.message ?? "Failed to refresh",
      color: "error",
    });
  }
}

async function onOpenDocument(path: string) {
  try {
    selectedFolderPath.value = null;
    await store.openDocument(path);
  } catch (err: any) {
    toast.add({
      title: "Error loading document",
      description: err?.message ?? "Failed to load",
      color: "error",
    });
  }
}

const pendingNewDocumentFolder = ref("/");
const selectedFolderPath = ref<string | null>(null);

function onSelectFolder(path: string) {
  selectedFolderPath.value = path;
}

/** Opens editor in new-doc mode for a given folder path (or root). */
function onAddDocumentAt(folderPath: string) {
  pendingNewDocumentFolder.value = folderPath;
  selectedFolderPath.value = null;
  store.newDocument();
}

const renameOpen = ref(false);
const renameFromPath = ref<string | null>(null);

function onRenameDocument(fromPath: string) {
  renameFromPath.value = fromPath;
  renameOpen.value = true;
}

async function onSubmitRename(toPath: string) {
  if (!renameFromPath.value) return;

  const fromPath = renameFromPath.value;

  try {
    await store.renameDocument(fromPath, toPath);

    if (loadedDocument.value?.path === fromPath) {
      await store.openDocument(toPath);
    }

    toast.add({ title: "Document renamed", color: "success" });
    renameOpen.value = false;
    renameFromPath.value = null;
  } catch (err: any) {
    toast.add({
      title: "Error renaming document",
      description: err?.message ?? "Failed to rename",
      color: "error",
    });
  }
}

async function onDeleteDocument(path: string) {
  try {
    await store.deleteDocument(path);

    if (loadedDocument.value?.path === path) {
      store.newDocument();
    }

    toast.add({ title: "Document deleted", color: "success" });
  } catch (err: any) {
    toast.add({
      title: "Error deleting document",
      description: err?.message ?? "Failed to delete",
      color: "error",
    });
  }
}

/**
 * Toggles a document's code-review exclusion. The store refreshes the tree and the
 * loaded document, so the editor badge and tree marker update without a manual reload.
 */
async function onToggleReviewExclusion(documentId: string, excluded: boolean) {
  try {
    await store.setReviewExclusion(documentId, excluded);
    toast.add({
      title: excluded
        ? "Excluded from code reviews"
        : "Included in code reviews",
      color: "success",
    });
  } catch (err: any) {
    toast.add({
      title: "Error updating document",
      description: err?.message ?? "Failed to update code-review exclusion",
      color: "error",
    });
  }
}

// ── Diff review flow (D-6) ──────────────────────────────────────────────

const diffOpen = ref(false);
const diffOriginal = ref("");
const diffNext = ref("");
/** Pending path for the save — set by the editor on review. */
const pendingSavePath = ref("");

/**
 * Opens the bottom diff drawer with original vs next markdown.
 * All saves go through this review step.
 */
function openDiff(original: string, next: string, path: string) {
  diffOriginal.value = original;
  diffNext.value = next;
  pendingSavePath.value = path;
  diffOpen.value = true;
}

/** Confirms the reviewed save and closes the drawer. */
async function onConfirmSave() {
  try {
    await store.saveDocument(pendingSavePath.value, diffNext.value);
    toast.add({ title: "Document saved", color: "success" });
    diffOpen.value = false;

    // Reload the document to reflect the saved state.
    if (pendingSavePath.value) {
      await store.openDocument(pendingSavePath.value);
    }
  } catch (err: any) {
    toast.add({
      title: "Error saving document",
      description: err?.message ?? "Failed to save",
      color: "error",
    });
  }
}

// ── Search panel ────────────────────────────────────────────────────────

const searchOpen = ref(false);

function openSearch() {
  searchOpen.value = true;
}

async function onTestSearch(query: string) {
  try {
    await store.testSearch(query);
  } catch (err: any) {
    toast.add({
      title: "Search error",
      description: err?.message ?? "Search failed",
      color: "error",
    });
  }
}

async function onTestSnippetSearch(
  query: string,
  kind: "section" | "code",
  excludeDocumentPaths: string[],
) {
  try {
    await store.testSnippetSearch(
      query,
      kind,
      excludeDocumentPaths.length > 0 ? excludeDocumentPaths : undefined,
    );
  } catch (err: any) {
    toast.add({
      title: "Search error",
      description: err?.message ?? "Search failed",
      color: "error",
    });
  }
}

// ── Parse preview panel ─────────────────────────────────────────────────

const parsePreviewOpen = ref(false);

async function onPreviewParse(path: string) {
  parsePreviewOpen.value = true;
  try {
    await store.previewDocumentParse(path);
  } catch (err: any) {
    toast.add({
      title: "Preview error",
      description: err?.message ?? "Failed to preview parse",
      color: "error",
    });
  }
}

watch(parsePreviewOpen, (isOpen) => {
  if (!isOpen) {
    store.clearParsePreview();
  }
});

// ── Init ────────────────────────────────────────────────────────────────

onMounted(async () => {
  // Load configured repositories in the background so the library form can
  // pre-select existing mappings. Errors are non-blocking since the form still
  // works without them (the checkbox group is simply hidden when empty).
  githubStore.loadRepositories().catch(() => undefined);
});

let libraryRouteSelectionRequestId = 0;

watch(
  () => route.params.libraryName,
  () => {
    void loadLibraryRouteSelection();
  },
  { immediate: true },
);

/**
 * After initial document load, select the root folder so the tree shows
 * it as selected and the editor panel displays top-level items.
 * If the library is empty (only root exists), show the add-new-document
 * panel instead of the empty folder browser.
 */
function selectRootOnFirstLoad() {
  if (documents.value.length > 0) {
    selectedFolderPath.value = "/";
  } else {
    // Empty library → show new document form
    store.newDocument();
  }
}

async function applyLibraryRouteSelection(requestId: number) {
  await store.loadLibraryList();
  if (requestId !== libraryRouteSelectionRequestId) {
    return;
  }

  const requestedName = routeLibraryName();
  const routeLibraryExists =
    requestedName !== null &&
    libraries.value.some((library) => library.name === requestedName);
  const activeLibraryStillExists =
    activeLibraryName.value !== null &&
    libraries.value.some((library) => library.name === activeLibraryName.value);
  const nextName = routeLibraryExists
    ? requestedName
    : activeLibraryStillExists
      ? activeLibraryName.value
      : (libraries.value[0]?.name ?? null);

  if (!nextName) {
    if (route.fullPath !== "/libraries") {
      await router.replace("/libraries");
    }
    if (requestId !== libraryRouteSelectionRequestId) {
      return;
    }

    store.clearLibrarySelection();
    return;
  }

  if (requestedName !== nextName) {
    await router.replace(libraryRoute(nextName));
    return;
  }

  await selectLibraryAndRoot(nextName);
}

async function loadLibraryRouteSelection() {
  const requestId = ++libraryRouteSelectionRequestId;
  librarySelectionLoading.value = true;

  try {
    await applyLibraryRouteSelection(requestId);
  } catch (err: any) {
    if (requestId !== libraryRouteSelectionRequestId) {
      return;
    }

    toast.add({
      title: "Error loading libraries",
      description: err?.message ?? "Failed to load",
      color: "error",
    });
  } finally {
    if (requestId === libraryRouteSelectionRequestId) {
      librarySelectionLoading.value = false;
    }
  }
}

async function selectLibraryAndRoot(name: string) {
  if (activeLibraryName.value === name) {
    await store.loadDocuments();
  } else {
    await store.selectLibrary(name);
  }

  selectRootOnFirstLoad();
}

async function replaceRouteWithActiveLibrary() {
  const nextRoute = activeLibraryName.value
    ? libraryRoute(activeLibraryName.value)
    : "/libraries";

  if (route.fullPath !== nextRoute) {
    await router.replace(nextRoute);
  }
}

function routeLibraryName() {
  const value = route.params.libraryName;
  if (Array.isArray(value)) {
    return value[0] ?? null;
  }

  return typeof value === "string" && value.length > 0 ? value : null;
}

function libraryRoute(name: string) {
  return `/libraries/${encodeURIComponent(name)}`;
}
</script>
