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
        :allow-remote-review-exclusion="
          activeLibraryAllowsRemoteDocumentOverrides
        "
        @select="onOpenDocument"
        @folder-select="onSelectFolder"
        @add="onAddDocumentAt"
        @rename="onRenameDocument"
        @delete="onDeleteDocument"
        @toggle-review-exclusion="onToggleReviewExclusion"
        @toggle-scoped-skill="onToggleScopedSkill"
        @refresh="onRefreshDocuments"
      />

      <!-- Editor panel (fills remaining space) -->
      <LibraryMetricsPanel
        v-if="showLibraryMetrics"
        class="min-w-0 flex-1"
        :window="libraryMetricsWindow"
        :document-read-series="documentReadSeries"
        :section-read-series="sectionReadSeries"
        :snippet-read-series="snippetReadSeries"
        :leaderboard="leaderboard"
        :section-leaderboard="sectionLeaderboard"
        :snippet-leaderboard="snippetLeaderboard"
        :loading-reads="loadingReads"
        :loading-leaderboard="loadingLeaderboard"
        :loading-section-leaderboard="loadingSectionLeaderboard"
        :loading-snippet-leaderboard="loadingSnippetLeaderboard"
        :refreshing="refreshingLibraryMetrics"
        :error="libraryMetricsError"
        @update:window="onLibraryMetricsWindowChange"
        @refresh="onRefreshLibraryMetrics"
      />
      <div v-else class="flex min-w-0 flex-1">
        <DocumentEditorPanel
          ref="editorPanelRef"
          class="min-w-0 flex-1"
          :document="loadedDocument"
          :loading="editorLoading"
          :paths="documentPaths"
          :selected-folder-path="selectedFolderPath"
          :initial-folder-path="pendingNewDocumentFolder"
          :actions-panel-open="editorActionsPanelOpen"
          @create-library="openLibraryForm(null)"
          @select-document="onOpenDocument"
          @select-folder="onSelectFolder"
          @review="openDiff"
          @save="onDirectSave"
          @toggle-actions-panel="
            editorActionsPanelOpen = !editorActionsPanelOpen
          "
        />

        <USidebar
          v-if="activeLibraryName && !selectedFolderPath"
          v-model:open="editorActionsPanelOpen"
          side="right"
          collapsible="icon"
          :style="{
            '--sidebar-width': '14.75rem',
            '--sidebar-width-icon': '3.5rem',
          }"
          :ui="{
            root: 'relative h-full',
            gap: 'h-full',
            container: 'absolute inset-y-0 end-0 z-10 flex h-full',
            inner: 'bg-default divide-transparent',
            body: 'p-0 sm:p-0',
          }"
        >
          <template #default="{ state }">
            <div class="flex h-full flex-col gap-2 p-2">
              <div
                v-if="selectedDocumentForActions"
                class="flex flex-col gap-1"
              >
                <UTooltip
                  text="Preview parse"
                  :content="{ side: 'left' }"
                  :delay-duration="0"
                >
                  <UButton
                    :label="state === 'expanded' ? 'Preview parse' : undefined"
                    icon="i-hugeicons-search-list-01"
                    size="md"
                    color="neutral"
                    variant="ghost"
                    :block="state === 'expanded'"
                    :ui="editorActionButtonUi(state)"
                    aria-label="Preview parse"
                    @click="onPreviewParse(selectedDocumentForActions.path)"
                  />
                </UTooltip>

                <UTooltip
                  v-if="activeDocumentRepoFileUrl"
                  text="View on GitHub"
                  :content="{ side: 'left' }"
                  :delay-duration="0"
                >
                  <UButton
                    :label="state === 'expanded' ? 'View on GitHub' : undefined"
                    icon="i-hugeicons-github"
                    size="md"
                    color="neutral"
                    variant="ghost"
                    :block="state === 'expanded'"
                    :ui="editorActionButtonUi(state)"
                    aria-label="View on GitHub"
                    :to="activeDocumentRepoFileUrl"
                    target="_blank"
                  />
                </UTooltip>

                <UPopover
                  v-if="selectedDocumentForActions"
                  mode="hover"
                  enable-touch
                  :open-delay="300"
                  :close-delay="150"
                  :content="{ side: 'left', align: 'start' }"
                  :ui="{ content: 'w-72' }"
                >
                  <UButton
                    :label="
                      state === 'expanded'
                        ? activeDocumentSkillStatusLabel
                        : undefined
                    "
                    :icon="
                      activeDocumentIsOrganizationSkill
                        ? 'i-hugeicons-ai-file'
                        : 'i-hugeicons-file-01'
                    "
                    size="md"
                    :color="
                      activeDocumentIsOrganizationSkill ? 'primary' : 'neutral'
                    "
                    variant="ghost"
                    :block="state === 'expanded'"
                    :ui="editorActionButtonUi(state)"
                    aria-label="Toggle organization skill"
                    @click="
                      onToggleScopedSkill(
                        selectedDocumentForActions.id,
                        activeDocumentNextScopedSkill,
                      )
                    "
                  />
                  <template #content>
                    <div class="p-3 text-sm text-muted">
                      Toggles whether this document is used as an organization
                      skill. Skills are exposed as MCP prompts and are excluded
                      from code-review retrieval.
                    </div>
                  </template>
                </UPopover>

                <UPopover
                  v-if="activeDocumentCanToggleReviewExclusion"
                  mode="hover"
                  enable-touch
                  :open-delay="300"
                  :close-delay="150"
                  :content="{ side: 'left', align: 'start' }"
                  :ui="{ content: 'w-72' }"
                >
                  <UButton
                    :label="
                      state === 'expanded'
                        ? activeDocumentReviewExclusionStatusLabel
                        : undefined
                    "
                    :icon="
                      activeDocumentReviewExcluded
                        ? 'i-hugeicons-view-off-slash'
                        : 'i-hugeicons-view'
                    "
                    size="md"
                    :color="
                      activeDocumentReviewExcluded ? 'warning' : 'neutral'
                    "
                    variant="ghost"
                    :block="state === 'expanded'"
                    :ui="editorActionButtonUi(state)"
                    aria-label="Toggle code-review exclusion"
                    :disabled="activeDocumentIsOrganizationSkill"
                    @click="onToggleActiveDocumentReviewExclusion"
                  />
                  <template #content>
                    <div class="p-3 text-sm text-muted">
                      Toggles whether code-review agents can retrieve this
                      document from list and search results. Direct reads and
                      normal library browsing remain available.
                    </div>
                  </template>
                </UPopover>
              </div>

              <USeparator v-if="selectedDocumentForActions" />

              <UTooltip
                v-if="!activeDocumentReadonly"
                :text="activeDocumentPrimarySaveLabel"
                :content="{ side: 'left' }"
                :delay-duration="0"
              >
                <UButton
                  :label="
                    state === 'expanded'
                      ? activeDocumentPrimarySaveLabel
                      : undefined
                  "
                  :icon="activeDocumentPrimarySaveIcon"
                  size="md"
                  color="neutral"
                  variant="ghost"
                  :block="state === 'expanded'"
                  :ui="editorActionButtonUi(state)"
                  :disabled="!editorCanReview"
                  :aria-label="activeDocumentPrimarySaveLabel"
                  @click="triggerEditorSaveAction"
                />
              </UTooltip>

              <div class="mt-auto flex flex-col gap-2">
                <div
                  v-if="selectedDocumentForActions"
                  class="flex items-center gap-2 px-2 py-1.5 text-muted"
                  :class="state === 'expanded' ? 'text-sm' : 'text-xs'"
                >
                  <UIcon name="i-hugeicons-coins-01" class="size-5 shrink-0" />
                  <span v-if="state === 'expanded'" class="truncate">
                    {{ activeDocumentTokenCountLabel }}
                  </span>
                </div>

                <USeparator v-if="selectedDocumentForActions" />
                <ZeeqPopConfirm
                  v-if="selectedDocumentForActions"
                  title="Delete Document"
                  :body="`Delete ${selectedDocumentForActions.path}?`"
                  confirm-label="Delete"
                  icon="i-hugeicons-delete-02"
                  :label="state === 'expanded' ? 'Delete' : undefined"
                  size="md"
                  color="error"
                  variant="ghost"
                  :block="state === 'expanded'"
                  :ui="editorActionButtonUi(state)"
                  aria-label="Delete document"
                  @confirm="onDeleteDocument(selectedDocumentForActions.path)"
                />
              </div>
            </div>
          </template>
        </USidebar>
      </div>
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
    <ReviewDiffDrawer
      ref="diffDrawerRef"
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

    <UModal
      v-model:open="unsavedChangesModalOpen"
      title="Save unsaved changes"
      :close="false"
      :dismissible="false"
    >
      <template #body>
        <p class="text-sm text-muted">
          This document has unsaved changes. Save them before leaving?
        </p>
      </template>

      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            label="Discard"
            color="neutral"
            variant="ghost"
            @click="discardUnsavedChanges"
          />
          <UButton
            label="Save"
            color="neutral"
            variant="subtle"
            @click="saveUnsavedChanges"
          />
        </div>
      </template>
    </UModal>
  </ZeeqView>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useIntervalFn, useStorage } from "@vueuse/core";
import { useLibraryStore } from "@/stores/library-store";
import { useLibraryMetricsStore } from "@/stores/library-metrics-store";
import { useGitHubSettingsStore } from "@/stores/github-settings-store";
import type { MetricWindowToken } from "@/stores/metrics-store";
import type { LibraryResponse } from "@/api/generated/types/LibraryResponse";
import type { IngestRunPageResponse } from "@/api/generated/types/IngestRunPageResponse";
import { libraryDocumentScopedSkillEnum } from "@/api/generated/types/LibraryDocumentScopedSkill";
import type { LibraryDocumentScopedSkill } from "@/api/generated/types/LibraryDocumentScopedSkill";
import LibrarySelector from "./LibrarySelector.vue";
import LibraryFormSlideover from "./LibraryFormSlideover.vue";
import DocumentTree from "./DocumentTree.vue";
import DocumentEditorPanel from "./DocumentEditorPanel.vue";
import ReviewDiffDrawer from "@/components/ReviewDiffDrawer.vue";
import DocumentSearchPanel from "./DocumentSearchPanel.vue";
import DocumentParsePreviewSlideover from "./DocumentParsePreviewSlideover.vue";
import DocumentRenameSlideover from "./DocumentRenameSlideover.vue";
import LibraryMetricsPanel from "./LibraryMetricsPanel.vue";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import { toGitHubWebUrl } from "@/utils/githubUrl";

type DocumentEditorPanelInstance = InstanceType<typeof DocumentEditorPanel>;
type ReviewDiffDrawerInstance = InstanceType<typeof ReviewDiffDrawer>;

const toast = useToast();
const store = useLibraryStore();
const libraryMetricsStore = useLibraryMetricsStore();
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

const {
  window: libraryMetricsWindow,
  documentReadSeries,
  sectionReadSeries,
  snippetReadSeries,
  leaderboard,
  sectionLeaderboard,
  snippetLeaderboard,
  error: libraryMetricsError,
  loadingReads,
  loadingLeaderboard,
  loadingSectionLeaderboard,
  loadingSnippetLeaderboard,
  refreshing: refreshingLibraryMetrics,
  activeOrganizationId: libraryMetricsOrganizationId,
} = storeToRefs(libraryMetricsStore);

const { configuredRepositories, librarySourceRepositories } =
  storeToRefs(githubStore);

const editorPanelRef = ref<DocumentEditorPanelInstance | null>(null);
const diffDrawerRef = ref<ReviewDiffDrawerInstance | null>(null);

const librarySelectionLoading = ref(false);
const editorActionsPanelOpen = useStorage(
  "zeeq:libraries:editor-actions-panel-open",
  false,
);
const unsavedChangesModalOpen = ref(false);
const pendingUnsavedChangesAction = ref<(() => void | Promise<void>) | null>(
  null,
);
const pendingNewDocumentFolder = ref("/");
const selectedFolderPath = ref<string | null>(null);
const editorLoading = computed(
  () =>
    librarySelectionLoading.value ||
    loadingLibraries.value ||
    loadingDocuments.value ||
    loadingDocument.value,
);

const showLibraryMetrics = computed(
  () =>
    !!activeLibraryName.value &&
    selectedFolderPath.value === "/" &&
    documents.value.length > 0,
);

watch(
  [
    showLibraryMetrics,
    activeLibraryName,
    libraryMetricsWindow,
    libraryMetricsOrganizationId,
  ],
  () => {
    if (showLibraryMetrics.value) {
      void libraryMetricsStore.loadMetrics(activeLibraryName.value);
    } else {
      libraryMetricsStore.clearMetrics();
    }
  },
  { immediate: true },
);

function onLibraryMetricsWindowChange(value: MetricWindowToken) {
  libraryMetricsWindow.value = value;
}

function onRefreshLibraryMetrics() {
  if (refreshingLibraryMetrics.value) {
    return;
  }

  void libraryMetricsStore.loadMetrics(activeLibraryName.value);
}

/**
 * Defers navigation-like actions while the active editor has unsaved changes.
 * The pending callback is replayed only after the user explicitly discards; Save
 * opens the existing review flow and leaves the user on the current document.
 */
async function confirmUnsavedChangesBefore(action: () => void | Promise<void>) {
  if (!editorHasUnsavedChanges.value) {
    return false;
  }

  pendingUnsavedChangesAction.value = action;
  unsavedChangesModalOpen.value = true;
  return true;
}

async function discardUnsavedChanges() {
  const action = pendingUnsavedChangesAction.value;
  pendingUnsavedChangesAction.value = null;
  unsavedChangesModalOpen.value = false;

  if (action) {
    await action();
  }
}

function saveUnsavedChanges() {
  pendingUnsavedChangesAction.value = null;
  unsavedChangesModalOpen.value = false;
  triggerEditorSaveAction();
}

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

/**
 * Remote documents in private-source libraries are still organization/library-owned
 * LibraryDocument rows, so their code-review exclusion flag is safe to mutate. Public-source
 * libraries use shared DocsPublicDocument rows and need a separate override model.
 */
const activeLibraryAllowsRemoteDocumentOverrides = computed(
  () =>
    libraries.value.find((library) => library.name === activeLibraryName.value)
      ?.source?.kind === "Private",
);

const editorCanReview = computed(
  () => editorPanelRef.value?.canReview ?? false,
);

const editorHasUnsavedChanges = computed(
  () => editorPanelRef.value?.hasChanges ?? false,
);

/**
 * The store keeps the last loaded document while the folder browser is open. Action controls must
 * follow the visible editor mode, so folder selection intentionally hides document-specific actions.
 */
const selectedDocumentForActions = computed(() =>
  selectedFolderPath.value ? null : loadedDocument.value,
);

/**
 * New-document mode has a path/content form but no persisted baseline. Keep the action panel open
 * and use a direct Save action instead of a diff-review flow.
 */
const isCreatingNewDocument = computed(
  () =>
    !!activeLibraryName.value &&
    !selectedFolderPath.value &&
    !loadedDocument.value,
);

watch(
  isCreatingNewDocument,
  (isCreating) => {
    if (isCreating) {
      editorActionsPanelOpen.value = true;
    }
  },
  { immediate: true },
);

/** Read-only gate for action controls rendered outside DocumentEditorPanel. */
const activeDocumentReadonly = computed(
  () => selectedDocumentForActions.value?.origin === "remote",
);

const activeDocumentIsOrganizationSkill = computed(
  () =>
    selectedDocumentForActions.value?.asScopedSkill ===
    libraryDocumentScopedSkillEnum.Organization,
);

/**
 * Skills are also review-excluded. Keep the manual flag distinct, but present one effective
 * sidebar status that matches review retrieval behavior.
 */
const activeDocumentReviewExcluded = computed(
  () =>
    Boolean(selectedDocumentForActions.value?.excludedFromCodeReviews) ||
    activeDocumentIsOrganizationSkill.value,
);

const activeDocumentCanToggleReviewExclusion = computed(() => {
  if (!selectedDocumentForActions.value) {
    return false;
  }

  return (
    selectedDocumentForActions.value.origin === "local" ||
    activeLibraryAllowsRemoteDocumentOverrides.value
  );
});

const activeDocumentNextScopedSkill = computed<LibraryDocumentScopedSkill>(
  () =>
    activeDocumentIsOrganizationSkill.value
      ? libraryDocumentScopedSkillEnum.None
      : libraryDocumentScopedSkillEnum.Organization,
);

const activeDocumentSkillStatusLabel = computed(() =>
  activeDocumentIsOrganizationSkill.value ? "Used as skill" : "Used as content",
);

const activeDocumentReviewExclusionStatusLabel = computed(() =>
  activeDocumentReviewExcluded.value
    ? "Excluded from reviews"
    : "Included in reviews",
);

const activeDocumentPrimarySaveLabel = computed(() =>
  isCreatingNewDocument.value ? "Save" : "Review and save",
);

const activeDocumentPrimarySaveIcon = computed(() =>
  isCreatingNewDocument.value
    ? "i-hugeicons-floppy-disk"
    : "i-hugeicons-checkmark-circle-02",
);

function editorActionButtonUi(state: "expanded" | "collapsed") {
  return {
    base: state === "expanded" ? "justify-start" : "justify-center",
  };
}

function onToggleActiveDocumentReviewExclusion() {
  if (!selectedDocumentForActions.value) {
    return;
  }

  void onToggleReviewExclusion(
    selectedDocumentForActions.value.id,
    !selectedDocumentForActions.value.excludedFromCodeReviews,
  );
}

const activeDocumentTokenCountLabel = computed(() => {
  const tokenCount = Number(selectedDocumentForActions.value?.tokenCount ?? 0);

  return `${new Intl.NumberFormat().format(tokenCount)} tokens`;
});

/** GitHub blob URL for the selected document, if its library is repository-driven. */
const activeDocumentRepoFileUrl = computed(() => {
  if (!activeLibraryRepoUrl.value || !selectedDocumentForActions.value?.path) {
    return null;
  }

  const encodedPath = selectedDocumentForActions.value.path
    .split("/")
    .map((segment) => encodeURIComponent(segment))
    .join("/");

  return `${toGitHubWebUrl(activeLibraryRepoUrl.value)}/blob/HEAD${encodedPath}`;
});

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
  if (await confirmUnsavedChangesBefore(() => performSelectLibrary(name))) {
    return;
  }

  await performSelectLibrary(name);
}

async function performSelectLibrary(name: string) {
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
  if (await confirmUnsavedChangesBefore(() => performOpenDocument(path))) {
    return;
  }

  await performOpenDocument(path);
}

async function performOpenDocument(path: string) {
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

async function onSelectFolder(path: string) {
  if (await confirmUnsavedChangesBefore(() => performSelectFolder(path))) {
    return;
  }

  performSelectFolder(path);
}

function performSelectFolder(path: string) {
  selectedFolderPath.value = path;
}

/** Opens editor in new-doc mode for a given folder path (or root). */
async function onAddDocumentAt(folderPath: string) {
  if (
    await confirmUnsavedChangesBefore(() => performAddDocumentAt(folderPath))
  ) {
    return;
  }

  performAddDocumentAt(folderPath);
}

function performAddDocumentAt(folderPath: string) {
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
  // NOTE: Deletion intentionally bypasses the unsaved-change modal. The delete confirmation is
  // already the destructive-action guard, and the user explicitly accepted this behavior.
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

async function onToggleScopedSkill(
  documentId: string,
  asScopedSkill: LibraryDocumentScopedSkill,
) {
  try {
    await store.setScopedSkill(documentId, asScopedSkill);
    toast.add({
      title:
        asScopedSkill === libraryDocumentScopedSkillEnum.Organization
          ? "Organization skill enabled"
          : "Organization skill removed",
      color: "success",
    });
  } catch (err: any) {
    toast.add({
      title: "Error updating document",
      description: err?.message ?? "Failed to update scoped-skill status",
      color: "error",
    });
  }
}

function triggerEditorSaveAction() {
  if (isCreatingNewDocument.value) {
    editorPanelRef.value?.triggerDirectSave();
    return;
  }

  editorPanelRef.value?.triggerReview();
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

/** Saves a new document directly; reviewing an empty-baseline diff adds no useful signal. */
async function onDirectSave(path: string, content: string) {
  try {
    await store.saveDocument(path, content);
    toast.add({ title: "Document saved", color: "success" });
    selectedFolderPath.value = null;
    await store.openDocument(path);
  } catch (err: any) {
    toast.add({
      title: "Error saving document",
      description: err?.message ?? "Failed to save",
      color: "error",
    });
  }
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

/**
 * CMD+S / CTRL+S triggers "Review and save" from the editor, or "Save
 * changes" when the review drawer is already open, instead of the browser's
 * save-page dialog.
 */
function handleGlobalKeydown(event: KeyboardEvent) {
  if (event.key.toLowerCase() !== "s" || !(event.metaKey || event.ctrlKey)) {
    return;
  }

  event.preventDefault();

  if (diffOpen.value) {
    diffDrawerRef.value?.triggerSave();
  } else {
    triggerEditorSaveAction();
  }
}

onMounted(() => {
  window.addEventListener("keydown", handleGlobalKeydown);
});

onBeforeUnmount(() => {
  window.removeEventListener("keydown", handleGlobalKeydown);
});

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
    selectedFolderPath.value = null;
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
