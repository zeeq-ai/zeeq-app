<template>
  <USlideover
    :open="open"
    side="right"
    :dismissible="true"
    :ui="{
      content: 'w-screen max-w-none lg:max-w-[min(1280px,calc(100vw-48px))]',
      body: 'p-0',
    }"
    @update:open="handleOpenUpdate"
  >
    <template #content>
      <div class="flex h-dvh min-h-0 flex-col overflow-hidden">
        <header
          class="grid min-w-0 shrink-0 grid-cols-[minmax(0,1fr)] gap-3 border-b border-default p-4 sm:px-6"
        >
          <div class="flex items-start justify-between gap-3">
            <div class="min-w-0">
              <h2 class="truncate text-lg font-semibold text-highlighted">
                Manage datasets
              </h2>
              <p
                class="mt-1 flex min-w-0 items-center gap-2 truncate text-sm text-muted"
              >
                <span class="truncate"
                  >{{ repositoryName }} · {{ dataset.name }}</span
                >
                <UBadge
                  :label="`v${dataset.version}`"
                  color="neutral"
                  variant="subtle"
                />
              </p>
            </div>
            <div class="flex shrink-0 items-center gap-2">
              <UDropdownMenu
                :items="datasetMenuItems"
                :content="{ align: 'end' }"
              >
                <UButton
                  icon="i-hugeicons-more-horizontal"
                  color="neutral"
                  variant="ghost"
                  aria-label="Dataset actions"
                />
              </UDropdownMenu>
              <UButton
                icon="i-hugeicons-cancel-01"
                color="neutral"
                variant="ghost"
                aria-label="Close dataset editor"
                @click="requestClose"
              />
            </div>
          </div>

          <div class="grid gap-3 md:grid-cols-2">
            <UFormField label="Dataset name" required
              ><UInput v-model="draft.name" maxlength="120" class="w-full"
            /></UFormField>
            <UFormField label="Description" hint="Optional"
              ><UInput
                v-model="draft.description"
                maxlength="300"
                class="w-full"
            /></UFormField>
          </div>

          <UFormField label="References for" class="w-full md:max-w-sm">
            <USelectMenu
              :model-value="props.selectedReviewerId ?? undefined"
              :items="reviewerItems"
              value-key="value"
              aria-label="Reviewer reference scope"
              :disabled="reviewerItems.length === 0"
              @update:model-value="emits('selectReviewer', $event)"
            />
          </UFormField>
        </header>

        <div
          class="flex min-h-0 flex-1 overflow-hidden"
          :class="isWide ? 'flex-row' : 'flex-col'"
        >
          <section
            v-if="isWide || !mobileDetailOpen || activePane === 'add-prs'"
            class="flex min-h-0 min-w-0 flex-1 flex-col"
            :class="
              isWide && activePane === 'in-dataset'
                ? 'max-w-[25rem] border-r border-default'
                : 'border-b border-default'
            "
          >
            <UTabs
              v-model="activePane"
              :items="paneItems"
              color="neutral"
              variant="link"
              class="min-h-0 flex-1"
              :ui="{
                root: 'flex h-full min-h-0 flex-col',
                list: 'shrink-0 px-4',
                content: 'min-h-0 flex-1 overflow-auto px-4 pb-3 pt-3',
              }"
            >
              <template #in-dataset>
                <div class="flex h-full min-h-0 flex-col gap-3">
                  <UInput
                    v-model="caseSearch"
                    icon="i-hugeicons-search-01"
                    placeholder="Search PRs..."
                    aria-label="Search dataset pull requests"
                  />
                  <p class="text-xs text-muted">
                    In dataset ({{ visibleCases.length }})
                  </p>
                  <div
                    class="min-h-0 flex-1 overflow-auto rounded-md border border-default"
                  >
                    <button
                      v-for="item in visibleCases"
                      :key="item.pullRequestNumber"
                      type="button"
                      class="flex w-full items-center gap-2 border-b border-default px-3 py-2 text-left hover:bg-elevated/50 focus-visible:outline-2 focus-visible:outline-primary"
                      :class="
                        selectedCase?.pullRequestNumber ===
                        item.pullRequestNumber
                          ? 'bg-primary/10'
                          : ''
                      "
                      :aria-current="
                        selectedCase?.pullRequestNumber ===
                        item.pullRequestNumber
                          ? 'true'
                          : undefined
                      "
                      @click="selectCase(item.pullRequestNumber)"
                    >
                      <span class="w-12 shrink-0 font-mono text-xs text-muted"
                        >#{{ item.pullRequestNumber }}</span
                      >
                      <UTooltip
                        :text="item.snapshot.title"
                        class="min-w-0 flex-1"
                        ><span
                          class="block truncate text-sm font-medium text-highlighted"
                          >{{ item.snapshot.title }}</span
                        ></UTooltip
                      >
                      <UBadge
                        :label="caseReferenceLabel(item)"
                        color="neutral"
                        variant="subtle"
                        size="xs"
                      />
                      <span
                        v-if="item.snapshot.omittedFileCount > 0"
                        class="shrink-0 text-[11px] text-warning"
                        >{{ item.snapshot.omittedFileCount }} omitted</span
                      >
                    </button>
                    <UEmpty
                      v-if="visibleCases.length === 0"
                      icon="i-hugeicons-git-pull-request"
                      title="No pull requests in this dataset"
                      description="Add PRs to create reusable evaluation cases."
                      class="py-10"
                    />
                  </div>
                </div>
              </template>
              <template #add-prs>
                <div class="h-full overflow-auto">
                  <DatasetAddPrsPanel
                    :targets="targets"
                    :staged-pull-request-numbers="
                      draft.cases
                        .filter((item) => !item.caseId)
                        .map((item) => item.pullRequestNumber)
                    "
                    :resolution-results="resolutionResults"
                    :loading="loadingTargets"
                    :loading-more="loadingMoreTargets"
                    :resolving="resolvingPrs"
                    :has-more="hasMoreTargets"
                    :disabled="saving"
                    @search-targets="searchTargets"
                    @load-more-targets="emits('loadMoreTargets')"
                    @resolve="emits('resolvePrs', $event)"
                  />
                </div>
              </template>
            </UTabs>
          </section>

          <section
            v-if="activePane === 'in-dataset' && !isWide && mobileDetailOpen"
            class="min-h-0 flex-1 overflow-y-auto p-4"
          >
            <UButton
              label="Back to PRs"
              icon="i-hugeicons-arrow-left-02"
              color="neutral"
              variant="ghost"
              class="mb-3 w-fit"
              @click="mobileDetailOpen = false"
            />
            <SelectedEvaluationCase
              v-if="selectedCase && selectedReferenceSet"
              v-bind="caseProps"
              @refresh="requestRefresh"
              @remove="removeSelectedCase"
              @load-snapshot="loadSnapshot"
              @import-historical="emitImportHistorical"
              @add-historical="addHistoricalFinding"
              @update-reference-set="updateSelectedReferenceSet"
            />
          </section>
          <section
            v-if="activePane === 'in-dataset' && isWide"
            class="min-h-0 min-w-0 flex-1 overflow-y-auto p-4 sm:p-6"
          >
            <SelectedEvaluationCase
              v-if="selectedCase && selectedReferenceSet"
              v-bind="caseProps"
              @refresh="requestRefresh"
              @remove="removeSelectedCase"
              @load-snapshot="loadSnapshot"
              @import-historical="emitImportHistorical"
              @add-historical="addHistoricalFinding"
              @update-reference-set="updateSelectedReferenceSet"
            />
            <UEmpty
              v-else
              icon="i-hugeicons-git-pull-request"
              title="Select a pull request"
              description="Choose a case to inspect its captured input and expected findings."
              class="py-12"
            />
          </section>
        </div>

        <footer
          class="sticky bottom-0 z-10 flex shrink-0 flex-wrap items-center justify-between gap-3 border-t border-default bg-default px-4 py-3 sm:px-6"
        >
          <p class="grid text-sm text-muted" aria-live="polite">
            <span>{{
              dirty
                ? `${changeCount} changes · Save as v${Number(dataset.version) + 1}`
                : "No changes"
            }}</span>
            <span
              >Saving creates v{{ Number(dataset.version) + 1 }}. Existing runs
              keep the dataset version they used.</span
            >
          </p>
          <div class="flex items-center gap-2">
            <UButton
              label="Cancel"
              color="neutral"
              variant="ghost"
              :disabled="saving"
              @click="requestClose"
            />
            <UButton
              :label="`Save as v${Number(dataset.version) + 1}`"
              icon="i-hugeicons-floppy-disk"
              color="primary"
              :loading="saving"
              :disabled="
                !dirty ||
                saving ||
                resolvingPrs ||
                !!refreshingCaseId ||
                !draft.name.trim()
              "
              @click="emits('save', false)"
            />
          </div>
        </footer>
      </div>
    </template>
  </USlideover>

  <UModal
    v-model:open="discardGuardOpen"
    title="Unsaved dataset changes"
    :close="false"
    :dismissible="false"
  >
    <template #body
      ><p class="text-sm text-muted">
        Save changes before leaving? A failed save keeps your draft open.
      </p></template
    >
    <template #footer>
      <div class="flex w-full flex-wrap justify-end gap-2">
        <UButton
          label="Keep editing"
          color="neutral"
          variant="ghost"
          @click="discardGuardOpen = false"
        />
        <UButton
          label="Discard"
          color="neutral"
          variant="outline"
          @click="discardAndClose"
        />
        <UButton
          label="Save"
          color="primary"
          :loading="saving"
          :disabled="resolvingPrs || !!refreshingCaseId"
          @click="saveAndClose"
        />
      </div>
    </template>
  </UModal>

  <UModal v-model:open="refreshGuardOpen" title="Refresh pull request snapshot">
    <template #body
      ><p class="text-sm text-muted">
        The fresh snapshot keeps this case ID and sets its reviewer references
        to Unlabelled. Existing runs keep their captured input and grade.
      </p></template
    >
    <template #footer
      ><div class="flex w-full justify-end gap-2">
        <UButton
          label="Cancel"
          color="neutral"
          variant="ghost"
          @click="refreshGuardOpen = false"
        /><UButton
          label="Refresh snapshot"
          color="warning"
          @click="confirmRefresh"
        /></div
    ></template>
  </UModal>

  <UModal v-model:open="archiveGuardOpen" title="Archive dataset?">
    <template #body
      ><p class="text-sm text-muted">
        This removes {{ dataset.name }} from the active dataset list. Existing
        run history and snapshots remain available.
      </p></template
    >
    <template #footer
      ><div class="flex w-full justify-end gap-2">
        <UButton
          label="Cancel"
          color="neutral"
          variant="ghost"
          @click="archiveGuardOpen = false"
        /><UButton
          label="Archive dataset"
          color="error"
          @click="
            emits('archiveDataset');
            archiveGuardOpen = false;
          "
        /></div
    ></template>
  </UModal>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useMediaQuery } from "@vueuse/core";
import type {
  CodeReviewEvaluationDatasetDetailDto,
  CodeReviewEvaluationPrResolutionDto,
  CodeReviewEvaluationPrTargetDto,
  CodeReviewEvaluationReferenceFindingDto,
  CodeReviewEvaluationReferenceSetDto,
  CodeReviewEvaluationSnapshot,
  CodeReviewerAgentDto,
  RefreshCodeReviewEvaluationCaseMutationResponse,
} from "@/api/generated";
import DatasetAddPrsPanel from "./DatasetAddPrsPanel.vue";
import SelectedEvaluationCase from "./SelectedEvaluationCase.vue";
import {
  addCapturedEvaluationCase,
  countEvaluationDatasetDraftChanges,
  createEvaluationDatasetDraft,
  refreshEvaluationCase,
  removeEvaluationCase,
  updateEvaluationCaseReferenceSet,
  type EvaluationCaseDraft,
  type EvaluationDatasetDraft,
} from "./evaluation-view-models";

const open = defineModel<boolean>("open", { required: true });
const draft = defineModel<EvaluationDatasetDraft>("draft", { required: true });
const props = defineProps<{
  dataset: CodeReviewEvaluationDatasetDetailDto;
  repositoryName: string;
  reviewers: CodeReviewerAgentDto[];
  selectedReviewerId: string | null;
  targets: CodeReviewEvaluationPrTargetDto[];
  resolutionResults: CodeReviewEvaluationPrResolutionDto[];
  refreshCaptures: Record<
    string,
    RefreshCodeReviewEvaluationCaseMutationResponse
  >;
  snapshots: Record<string, CodeReviewEvaluationSnapshot>;
  focusPullRequestNumber?: number | null;
  historicalFindings: CodeReviewEvaluationReferenceFindingDto[];
  historicalLookupComplete: boolean;
  loadingHistorical: boolean;
  loadingTargets: boolean;
  loadingMoreTargets: boolean;
  resolvingPrs: boolean;
  hasMoreTargets: boolean;
  snapshotLoading: boolean;
  refreshingCaseId: string | null;
  saving: boolean;
  saveError: string | null;
}>();
const emits = defineEmits<{
  selectReviewer: [reviewerId: string];
  searchTargets: [query: string, state: string];
  loadMoreTargets: [];
  resolvePrs: [entries: string[]];
  refreshCase: [caseId: string];
  save: [closeAfterSuccess: boolean];
  close: [];
  createDataset: [];
  archiveDataset: [];
  loadSnapshot: [caseId: string];
  importHistorical: [caseId: string];
  caseSelected: [caseId: string | null, pullRequestNumber: number | null];
}>();

const paneItems = [
  { label: "In dataset", value: "in-dataset", slot: "in-dataset" },
  { label: "Add PRs", value: "add-prs", slot: "add-prs" },
];
const activePane = ref("in-dataset");
const caseSearch = ref("");
const selectedPullRequestNumber = ref<number | null>(null);
const discardGuardOpen = ref(false);
const refreshGuardOpen = ref(false);
const archiveGuardOpen = ref(false);
const mobileDetailOpen = ref(false);
const isWide = useMediaQuery("(min-width: 960px)");
const appliedRefreshTokens = new Set<string>();
const dirty = computed(() => changeCount.value > 0);
const changeCount = computed(
  () =>
    countEvaluationDatasetDraftChanges(draft.value) +
    Number(draft.value.name !== props.dataset.name) +
    Number(draft.value.description !== (props.dataset.description ?? "")),
);
const reviewerItems = computed(() =>
  props.reviewers.map((reviewer) => ({
    label: `${reviewer.displayName} · ${referenceCountForReviewer(reviewer.id)} references`,
    value: reviewer.id,
  })),
);
const datasetMenuItems = computed(() => [
  [
    {
      label: "New dataset",
      icon: "i-hugeicons-plus-sign",
      disabled: dirty.value || props.saving,
      onSelect: () => emits("createDataset"),
    },
    {
      label: "Archive dataset",
      icon: "i-hugeicons-archive-02",
      color: "error" as const,
      disabled: dirty.value || props.saving,
      onSelect: () => {
        archiveGuardOpen.value = true;
      },
    },
  ],
]);
const visibleCases = computed(() => {
  const search = caseSearch.value.trim().toLocaleLowerCase();
  return draft.value.cases.filter(
    (item) =>
      !search ||
      String(item.pullRequestNumber).includes(search) ||
      item.snapshot.title.toLocaleLowerCase().includes(search),
  );
});
const selectedCase = computed(
  () =>
    visibleCases.value.find(
      (item) => item.pullRequestNumber === selectedPullRequestNumber.value,
    ) ?? null,
);
const selectedReferenceSet =
  computed<CodeReviewEvaluationReferenceSetDto | null>(() => {
    if (!selectedCase.value || !props.selectedReviewerId) return null;
    return (
      selectedCase.value.referenceSets.find(
        (item) => item.reviewerAgentId === props.selectedReviewerId,
      ) ?? {
        reviewerAgentId: props.selectedReviewerId,
        state: "Unlabelled",
        findings: [],
      }
    );
  });
const caseProps = computed(() => ({
  evaluationCase: selectedCase.value as EvaluationCaseDraft,
  referenceSet:
    selectedReferenceSet.value as CodeReviewEvaluationReferenceSetDto,
  snapshot:
    selectedCase.value?.caseId && !selectedCase.value.captureToken
      ? (props.snapshots[selectedCase.value.caseId] ?? null)
      : null,
  snapshotLoading:
    !!selectedCase.value?.caseId &&
    !selectedCase.value.captureToken &&
    props.snapshotLoading,
  historicalFindings: props.historicalFindings,
  historicalLookupComplete: props.historicalLookupComplete,
  loadingHistorical: props.loadingHistorical,
  saving: props.saving,
  refreshing: props.refreshingCaseId === selectedCase.value?.caseId,
  saveError: props.saveError,
}));

watch(
  () => props.dataset.id,
  () => {
    // The parent can mount this editor with unsaved changes or an adopted finding already
    // staged. Opening the same dataset must not replace that draft with the saved baseline.
    if (draft.value.datasetId !== props.dataset.id) {
      draft.value = createEvaluationDatasetDraft(props.dataset);
    }
    selectedPullRequestNumber.value =
      draft.value.cases[0]?.pullRequestNumber ?? null;
    activePane.value = "in-dataset";
    mobileDetailOpen.value = false;
    const selected = draft.value.cases[0];
    emits(
      "caseSelected",
      selected?.caseId ?? null,
      selected?.pullRequestNumber ?? null,
    );
  },
  { immediate: true },
);

watch(
  () => [open.value, props.focusPullRequestNumber],
  ([isOpen, number]) => {
    if (isOpen && typeof number === "number") selectCase(number);
  },
  { immediate: true },
);

watch(
  () => [selectedCase.value?.caseId, selectedCase.value?.captureToken],
  ([caseId, captureToken]) => {
    if (caseId && !captureToken) emits("loadSnapshot", caseId);
  },
  { immediate: true },
);

watch(
  () => [
    selectedCase.value?.caseId,
    selectedCase.value?.pullRequestNumber,
    props.selectedReviewerId,
  ],
  () => {
    emits(
      "caseSelected",
      selectedCase.value?.caseId ?? null,
      selectedCase.value?.pullRequestNumber ?? null,
    );
  },
  { immediate: true },
);

watch(
  () => props.resolutionResults,
  (items) => {
    let nextDraft = draft.value;
    for (const item of items) {
      if (!item.captureToken || !item.preview) continue;
      const pullRequestNumber = Number(item.preview.pullRequestNumber);
      nextDraft = addCapturedEvaluationCase(
        nextDraft,
        {
          pullRequestNumber,
          title: item.target?.title ?? item.preview.title,
          captureToken: item.captureToken,
          preview: item.preview,
        },
        props.dataset.cases.find(
          (evaluationCase) =>
            Number(evaluationCase.pullRequestNumber) === pullRequestNumber,
        ),
        props.reviewers.map((reviewer) => reviewer.id),
      );
      selectedPullRequestNumber.value = pullRequestNumber;
    }
    draft.value = nextDraft;
  },
);

watch(
  () => props.refreshCaptures,
  (captures) => {
    let nextDraft = draft.value;
    for (const [caseId, capture] of Object.entries(captures)) {
      if (appliedRefreshTokens.has(capture.captureToken) || !capture.preview)
        continue;
      appliedRefreshTokens.add(capture.captureToken);
      nextDraft = refreshEvaluationCase(nextDraft, caseId, {
        captureToken: capture.captureToken,
        title: capture.preview.title,
        preview: capture.preview,
      });
    }
    draft.value = nextDraft;
  },
);

function referenceCountForReviewer(reviewerId: string): number {
  return draft.value.cases.reduce(
    (total, item) =>
      total +
      (item.referenceSets.find((set) => set.reviewerAgentId === reviewerId)
        ?.findings.length ?? 0),
    0,
  );
}
function searchTargets(query: string) {
  emits("searchTargets", query, "");
}
function selectCase(pullRequestNumber: number) {
  selectedPullRequestNumber.value = pullRequestNumber;
  mobileDetailOpen.value = true;
}
function caseReferenceLabel(item: EvaluationCaseDraft): string {
  const set = item.referenceSets.find(
    (reference) => reference.reviewerAgentId === props.selectedReviewerId,
  );
  if (!set || set.state === "Unlabelled") return "Unlabelled";
  return set.state === "ReviewedClean"
    ? "Reviewed clean"
    : `${set.findings.length} ${set.findings.length === 1 ? "finding" : "findings"}`;
}
function updateSelectedReferenceSet(
  referenceSet: CodeReviewEvaluationReferenceSetDto,
) {
  if (!selectedCase.value) return;
  if (selectedCase.value.caseId) {
    draft.value = updateEvaluationCaseReferenceSet(
      draft.value,
      selectedCase.value.caseId,
      referenceSet,
    );
    return;
  }
  draft.value = {
    ...draft.value,
    cases: draft.value.cases.map((item) =>
      item.pullRequestNumber === selectedCase.value?.pullRequestNumber
        ? {
            ...item,
            referenceSets: [
              ...item.referenceSets.filter(
                (set) => set.reviewerAgentId !== referenceSet.reviewerAgentId,
              ),
              referenceSet,
            ],
          }
        : item,
    ),
  };
}
function requestRefresh() {
  refreshGuardOpen.value = true;
}
function confirmRefresh() {
  refreshGuardOpen.value = false;
  if (selectedCase.value?.caseId)
    emits("refreshCase", selectedCase.value.caseId);
}
function removeSelectedCase() {
  if (!selectedCase.value) return;
  const number = selectedCase.value.pullRequestNumber;
  if (selectedCase.value.caseId)
    draft.value = removeEvaluationCase(draft.value, selectedCase.value.caseId);
  else
    draft.value = {
      ...draft.value,
      cases: draft.value.cases.filter(
        (item) => item.pullRequestNumber !== number,
      ),
    };
  selectedPullRequestNumber.value =
    draft.value.cases[0]?.pullRequestNumber ?? null;
  mobileDetailOpen.value = false;
}
function handleOpenUpdate(nextOpen: boolean) {
  if (nextOpen) {
    open.value = true;
    return;
  }
  requestClose();
}
function requestClose() {
  if (dirty.value) {
    discardGuardOpen.value = true;
    return;
  }
  open.value = false;
  emits("close");
}
function discardAndClose() {
  draft.value = createEvaluationDatasetDraft(props.dataset);
  discardGuardOpen.value = false;
  open.value = false;
  emits("close");
}
function saveAndClose() {
  discardGuardOpen.value = false;
  emits("save", true);
}
function loadSnapshot() {
  if (selectedCase.value?.caseId)
    emits("loadSnapshot", selectedCase.value.caseId);
}
function emitImportHistorical() {
  if (selectedCase.value?.caseId)
    emits("importHistorical", selectedCase.value.caseId);
}
function addHistoricalFinding(
  finding: CodeReviewEvaluationReferenceFindingDto,
) {
  if (!selectedReferenceSet.value || !selectedCase.value) return;
  updateSelectedReferenceSet({
    ...selectedReferenceSet.value,
    state: "Expected",
    findings: [
      ...selectedReferenceSet.value.findings,
      { ...finding, id: crypto.randomUUID(), origin: "HistoricalReview" },
    ],
  });
}
</script>
