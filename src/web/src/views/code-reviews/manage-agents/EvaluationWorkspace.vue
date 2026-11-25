<template>
  <div class="flex h-full min-h-0 flex-col overflow-hidden">
    <EvaluationToolbar
      v-if="availability?.enabled"
      :datasets="datasets"
      :selected-dataset-id="selectedDatasetId"
      :revisions="revisions"
      :selected-revision-id="selectedRevisionId"
      :disabled="
        !canManage ||
        !agentId ||
        datasetDraftDirty ||
        loadingAvailability ||
        loadingDatasets
      "
      :running="submittingRun"
      @select-dataset="selectDataset"
      @select-revision="selectRevision"
      @manage-datasets="openDatasetEditor"
      @create-dataset="startNewDataset"
      @view-revision="openRevisionDiff"
      @run="runEvaluation"
    />

    <div v-if="availabilityError" class="border-b border-default p-3">
      <UAlert
        title="Evaluation availability could not be checked"
        :description="availabilityError"
        color="error"
        variant="subtle"
      />
    </div>

    <div
      v-if="targetError && datasetEditorOpen"
      class="border-b border-default p-3"
    >
      <UAlert
        title="Pull requests could not be loaded"
        :description="targetError"
        color="error"
        variant="subtle"
      />
    </div>

    <div
      v-if="
        !availability?.enabled && !availabilityError && !loadingAvailability
      "
      class="border-b border-default p-3"
    >
      <UAlert
        title="Reviewer evaluations are disabled"
        description="Contact your administrator to enable reusable datasets and run history. You can still run a single-PR test from the Test tab."
        color="neutral"
        variant="subtle"
      />
    </div>

    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div v-if="showCreateDataset" class="min-h-0 flex-1 overflow-y-auto">
        <EvaluationCreateDataset
          :creating="creatingDataset"
          :error="createError"
          @create="createDataset"
        />
      </div>

      <div
        v-else-if="loadingAvailability || availability?.enabled"
        class="min-h-0 flex-1 overflow-y-auto p-3 sm:p-4"
      >
        <div v-if="loadingAvailability" class="grid gap-3 p-4">
          <USkeleton class="h-10" />
          <USkeleton class="h-48" />
        </div>

        <div v-else-if="loadingDatasets && !selectedRun" class="grid gap-3">
          <USkeleton class="h-14" />
          <USkeleton class="h-48" />
        </div>

        <EvaluationCreateDataset
          v-else-if="datasets.length === 0"
          :creating="creatingDataset"
          :error="createError"
          @create="createDataset"
        />

        <div v-else-if="selectedRun" class="grid gap-4">
          <div
            v-if="datasetDraftDirty && !datasetEditorOpen"
            class="sticky top-0 z-10 flex flex-wrap items-center gap-2 rounded-md border border-default bg-default p-3 shadow-sm"
          >
            <span class="text-sm"
              >{{ draftChangeCount }} unsaved dataset
              {{ draftChangeCount === 1 ? "change" : "changes" }}</span
            >
            <UButton
              label="Review in Manage datasets"
              color="neutral"
              variant="outline"
              size="xs"
              @click="openDatasetEditor()"
            />
            <UButton
              :label="`Save as v${Number(datasetDraft?.expectedVersion ?? 0) + 1}`"
              color="primary"
              size="xs"
              :loading="savingDataset"
              @click="saveDatasetDraft(false)"
            />
            <UButton
              label="Discard"
              color="neutral"
              variant="ghost"
              size="xs"
              :disabled="savingDataset"
              @click="discardDraftConfirmOpen = true"
            />
            <p v-if="datasetSaveError" class="w-full text-xs text-error">
              {{ datasetSaveError }}
            </p>
          </div>
          <header class="flex flex-wrap items-start justify-between gap-3">
            <div>
              <div class="flex flex-wrap items-center gap-2">
                <UTooltip :text="selectedRun.run.id"
                  ><h2 class="font-mono text-sm font-semibold text-highlighted">
                    Evaluation {{ selectedRun.run.id.slice(-8) }}
                  </h2></UTooltip
                >
                <UButton
                  label="Copy run ID"
                  icon="i-hugeicons-copy-01"
                  size="xs"
                  color="neutral"
                  variant="ghost"
                  @click="copyRunId(selectedRun.run.id)"
                />
              </div>
              <p class="mt-1 text-sm text-muted">
                {{ datasetName(selectedRun.run.datasetId) }} · v{{
                  selectedRun.run.datasetVersion
                }}
                · {{ revisionCaption(selectedRun.run) }} ·
                {{ formatDate(selectedRun.run.createdAtUtc) }}
              </p>
              <p class="mt-1 text-xs text-muted">
                {{ provenanceCaption(selectedRun.cases) }}
              </p>
            </div>
            <div class="flex gap-2">
              <UBadge
                :label="selectedRun.run.state"
                :color="runStateColor(selectedRun.run.state)"
                variant="subtle"
              />
              <UButton
                label="Back to latest"
                color="neutral"
                variant="ghost"
                @click="backToLatest"
              />
            </div>
          </header>

          <div
            class="flex flex-wrap items-center gap-x-4 gap-y-1 border-y border-default py-2 text-xs"
          >
            <span class="text-default"
              >Coverage
              <strong class="font-mono tabular-nums">{{
                coverageLabel(selectedRun.run)
              }}</strong></span
            >
            <span v-if="runFalsePositives" class="text-default"
              >Known FPs
              <strong
                :class="
                  runFalsePositives.reported > 0
                    ? 'text-warning font-medium'
                    : 'text-muted'
                "
                >{{
                  runFalsePositives.checked === 0 &&
                  runFalsePositives.unchecked === 0
                    ? "No known false positives checked"
                    : `${runFalsePositives.reported} of ${runFalsePositives.checked} reported`
                }}</strong
              ><span
                v-if="runFalsePositives.onUnlabelledPrCount > 0"
                class="text-muted"
              >
                ({{ runFalsePositives.onUnlabelledPrCount }} on unlabelled
                {{
                  runFalsePositives.onUnlabelledPrCount === 1 ? "PR" : "PRs"
                }})</span
              ><span v-if="runFalsePositives.unchecked > 0" class="text-muted">
                · {{ runFalsePositives.unchecked }} not checked</span
              ></span
            >
            <span class="text-default"
              >Graded
              <strong class="font-mono tabular-nums"
                >{{ selectedRun.run.counts.gradedCaseCount }}/{{
                  selectedRun.run.counts.caseCount
                }}</strong
              ></span
            >
            <span class="text-default"
              >Skipped
              <strong class="font-mono tabular-nums">{{
                selectedRun.run.counts.skippedCaseCount
              }}</strong></span
            >
            <span class="text-default"
              >Failed
              <strong class="font-mono tabular-nums">{{
                selectedRun.run.counts.failedCaseCount
              }}</strong></span
            >
            <span
              v-if="runFalsePositives?.onUnlabelledPrCount"
              class="text-default"
            >
              FP-only PRs
              <strong class="font-mono tabular-nums">{{
                runFalsePositives.onUnlabelledPrCount
              }}</strong>
            </span>
            <span class="text-default">
              Unlabelled
              <strong class="font-mono tabular-nums">{{
                selectedRun.run.counts.unlabelledCaseCount
              }}</strong>
            </span>
            <span class="text-default"
              >Additional
              <strong class="font-mono tabular-nums"
                >{{
                  selectedRun.run.counts.additionalFindingCount
                }}
                unverified</strong
              ></span
            >
            <UBadge
              v-if="selectedRun.run.state === 'Incomplete'"
              label="Incomplete"
              color="warning"
              variant="subtle"
              size="xs"
            />
          </div>
          <p class="-mt-3 text-xs text-muted">
            Coverage excludes failed, skipped and unlabelled cases.
          </p>

          <EvaluationRunCases
            :cases="selectedRun.cases"
            :loading="loadingRun"
            :can-manage="canManage && !savingDataset"
            @add-to-reference="stageFinding"
            @mark-as-false-positive="openFalsePositiveModal"
          />

          <EvaluationRunHistory
            class="border-t border-default pt-3"
            :runs="runs"
            :loading="loadingRuns"
            :next-cursor-id="runNextCursorId"
            :dataset-name="datasetName"
            @select="viewRun"
            @load-more="loadMoreRuns"
            @cancel="cancelRun"
            @abandon="abandonRun"
            @retry="retryFailedRun"
          />
        </div>

        <div v-else class="grid gap-4">
          <header class="flex flex-wrap items-center justify-between gap-3">
            <div class="min-w-0">
              <h2 class="truncate text-base font-semibold text-highlighted">
                {{ selectedDataset?.name ?? "Evaluation" }}
              </h2>
              <p class="mt-1 text-sm text-muted">
                {{
                  selectedDataset
                    ? `Saved dataset v${selectedDataset.version} · ${selectedDataset.caseCount} PRs`
                    : "Select a dataset to view saved runs."
                }}
                <span v-if="selectedRevisionId">
                  · Run uses saved revision</span
                >
                <span v-else> · Run uses current draft</span>
              </p>
            </div>
            <UButton
              label="Refresh history"
              icon="i-hugeicons-refresh"
              color="neutral"
              variant="ghost"
              :loading="loadingRuns"
              @click="refreshHistory"
            />
          </header>

          <UAlert
            v-if="runError"
            :title="
              runConflict
                ? 'Another evaluation is already active'
                : 'Evaluation request failed'
            "
            :description="
              runConflict ? `${runError} Active run: ${runConflict}` : runError
            "
            :color="runConflict ? 'warning' : 'error'"
            variant="subtle"
          >
            <template v-if="runConflict" #actions>
              <UButton
                label="View active run"
                size="xs"
                color="neutral"
                variant="outline"
                @click="viewRun(runConflict)"
              />
            </template>
          </UAlert>

          <EvaluationRunHistory
            :runs="runs"
            :loading="loadingRuns"
            :next-cursor-id="runNextCursorId"
            :dataset-name="datasetName"
            @select="viewRun"
            @load-more="loadMoreRuns"
            @cancel="cancelRun"
            @abandon="abandonRun"
            @retry="retryFailedRun"
          />
        </div>
      </div>
    </div>

    <DatasetSlideover
      v-if="selectedDataset && datasetDraft"
      v-model:open="datasetEditorOpen"
      v-model:draft="datasetDraft"
      v-model:selected-numbers="selectedPullRequestNumbers"
      :dataset="selectedDataset"
      :repository-name="workspaceRepository.name"
      :reviewers="reviewers"
      :selected-reviewer-id="selectedReviewerId"
      :targets="targetRows"
      :resolution-results="resolutionResults"
      :refresh-captures="refreshCaptures"
      :snapshots="caseSnapshots"
      :focus-pull-request-number="referenceFocusPr"
      :historical-findings="historicalFindings"
      :historical-lookup-complete="historicalLookupComplete"
      :loading-historical="loadingHistorical"
      :snapshot-loading="!!loadingSnapshotCaseId"
      :loading-targets="loadingTargets"
      :loading-more-targets="loadingMoreTargets"
      :resolving-prs="resolvingPrs"
      :has-more-targets="!!targetNextCursor"
      :refreshing-case-id="refreshingCaseId"
      :saving="savingDataset"
      :save-error="selectionSaveError ?? datasetSaveError"
      @select-reviewer="selectReviewer"
      @search-targets="searchTargets"
      @load-more-targets="loadMoreTargets"
      @resolve-prs="resolvePrs"
      @refresh-case="refreshCase"
      @save="saveDatasetDraft"
      @load-snapshot="loadCaseSnapshot"
      @import-historical="loadHistoricalFindings"
      @archive-dataset="archiveDataset"
      @create-dataset="startNewDataset"
      @case-selected="setHistoricalScope"
      @close="closeDatasetEditor"
    />

    <UModal
      v-model:open="falsePositiveModalOpen"
      title="Mark as false positive"
      description="Future evals will flag this claim if this reviewer reports it again on this snapshot."
    >
      <template #body>
        <div class="grid gap-3">
          <UAlert
            v-if="stagingError"
            :description="stagingError"
            color="error"
            variant="subtle"
          />
          <p class="text-sm font-medium">
            {{ pendingFalsePositive?.candidate.summary }}
          </p>
          <p class="font-mono text-xs text-muted">
            {{ pendingFalsePositive?.candidate.file
            }}<span v-if="pendingFalsePositive?.candidate.line"
              >:{{ pendingFalsePositive.candidate.line }}</span
            >
          </p>
          <UFormField
            label="Rationale"
            hint="Optional — helps the judge match accurately"
            ><UTextarea
              v-model="falsePositiveRationale"
              :maxlength="1000"
              placeholder="Why is this wrong?"
              class="w-full"
          /></UFormField>
          <p class="text-sm text-muted">
            Future evals will flag this claim if this reviewer reports it again
            on this snapshot.
          </p>
        </div>
      </template>
      <template #footer
        ><div class="flex w-full justify-end gap-2">
          <UButton
            label="Cancel"
            color="neutral"
            variant="ghost"
            @click="falsePositiveModalOpen = false"
          /><UButton
            label="Mark as false positive"
            color="primary"
            :loading="stagingFalsePositive"
            @click="submitFalsePositive"
          /></div
      ></template>
    </UModal>

    <UModal
      v-model:open="discardDraftConfirmOpen"
      title="Discard dataset changes?"
      description="This discards every unsaved change in the current dataset draft, including all staged run findings."
    >
      <template #footer
        ><div class="flex w-full justify-end gap-2">
          <UButton
            label="Keep changes"
            color="neutral"
            variant="ghost"
            @click="discardDraftConfirmOpen = false"
          /><UButton
            label="Discard all changes"
            color="error"
            @click="confirmDiscardDatasetDraft"
          /></div
      ></template>
    </UModal>

    <EvaluationRevisionDiff
      v-if="selectedRevision"
      v-model:open="revisionDiffOpen"
      :revision="selectedRevision"
      :current="reviewerConfiguration"
    />
  </div>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { onBeforeUnmount, onMounted, ref, watch } from "vue";
import {
  onBeforeRouteLeave,
  onBeforeRouteUpdate,
  useRoute,
  useRouter,
} from "vue-router";
import type {
  CodeReviewerAgentDto,
  CodeReviewerAgentRevisionConfiguration,
  CodeReviewEvaluationFindingSeverity,
  CodeReviewEvaluationReferenceFindingDto,
  EvaluationRunCaseDto,
  EvaluationRunDto,
} from "@/api/generated";
import { useCodeReviewEvaluationStore } from "@/stores/code-review-evaluation-store";
import { CodeReviews } from "@/api/generated";
import DatasetSlideover from "./DatasetSlideover.vue";
import EvaluationCreateDataset from "./EvaluationCreateDataset.vue";
import EvaluationRunCases from "./EvaluationRunCases.vue";
import EvaluationRunHistory from "./EvaluationRunHistory.vue";
import EvaluationRevisionDiff from "./EvaluationRevisionDiff.vue";
import EvaluationToolbar from "./EvaluationToolbar.vue";
import {
  addCapturedEvaluationCase,
  countEvaluationDatasetDraftChanges,
  findStagedFindingDisposition,
  summarizeRunFalsePositives,
  createEvaluationDatasetDraft,
  updateEvaluationCaseReferenceSet,
  type EvaluationDatasetDraft,
} from "./evaluation-view-models";

const props = defineProps<{
  workspaceRepository: { id: string; name: string };
  agentId: string | null;
  reviewers: CodeReviewerAgentDto[];
  reviewerConfiguration: CodeReviewerAgentRevisionConfiguration;
  canManage: boolean;
}>();

const store = useCodeReviewEvaluationStore();
const route = useRoute();
const router = useRouter();
const toast = useToast();
const {
  availability,
  availabilityError,
  datasets,
  selectedDataset,
  targetRows,
  targetNextCursor,
  resolutionResults,
  refreshCaptures,
  caseSnapshots,
  loadingSnapshotCaseId,
  revisions,
  selectedRevision,
  runs,
  selectedRun,
  runNextCursorId,
  loadingAvailability,
  loadingDatasets,
  loadingRuns,
  loadingRun,
  creatingDataset,
  savingDataset,
  loadingTargets,
  loadingMoreTargets,
  resolvingPrs,
  submittingRun,
  datasetError,
  targetError,
  saveErrorCode,
  createError,
  runError,
  runConflict,
} = storeToRefs(store);

const selectedDatasetId = ref<string | null>(null);
const selectedRevisionId = ref<string | null>(null);
const selectedReviewerId = ref<string | null>(null);
const referenceFocusPr = ref<number | null>(null);
const historicalFindings = ref<CodeReviewEvaluationReferenceFindingDto[]>([]);
const loadingHistorical = ref(false);
const historicalLookupComplete = ref(false);
const historicalScope = ref<{
  caseId: string | null;
  pullRequestNumber: number | null;
  reviewerId: string | null;
}>({ caseId: null, pullRequestNumber: null, reviewerId: null });
let historicalRequestId = 0;
let refreshUiRequestId = 0;
const revisionDiffOpen = ref(false);
const falsePositiveModalOpen = ref(false);
const falsePositiveRationale = ref("");
const discardDraftConfirmOpen = ref(false);
const stagingFalsePositive = ref(false);
const stagingError = ref<string | null>(null);
const pendingFalsePositive = ref<{
  result: EvaluationRunCaseDto;
  candidate: EvaluationRunCaseDto["candidateFindings"][number];
  runId: string;
  datasetId: string;
} | null>(null);
const datasetDraft = ref<EvaluationDatasetDraft | null>(null);
const selectedPullRequestNumbers = ref(new Set<number>());
const selectionSaveError = ref<string | null>(null);
const datasetDraftEditEpoch = ref(0);
const datasetEditorOpen = ref(false);
let datasetEditorSessionId = 0;
const showCreateDataset = ref(false);
let pollHandle: ReturnType<typeof setInterval> | null = null;
let inboxIntentRequestId = 0;
let inboxIntentReady = false;
let pendingInboxCleanupPath: string | null = null;
let repositoryLoadRequestId = 0;
let repositoryLoadDeferred = false;
let workspaceUnmounted = false;

watch(
  datasetEditorOpen,
  () => {
    datasetEditorSessionId += 1;
  },
  { flush: "sync" },
);

watch(
  datasetDraft,
  () => {
    datasetDraftEditEpoch.value += 1;
  },
  { deep: true, flush: "sync" },
);

const datasetDraftDirty = computed(() => {
  if (!datasetDraft.value || !selectedDataset.value) {
    return false;
  }
  return (
    selectedPullRequestNumbers.value.size > 0 ||
    datasetDraft.value.name !== selectedDataset.value.name ||
    datasetDraft.value.description !==
      (selectedDataset.value.description ?? "") ||
    countEvaluationDatasetDraftChanges(datasetDraft.value) > 0
  );
});
const draftChangeCount = computed(() =>
  datasetDraft.value
    ? countEvaluationDatasetDraftChanges(datasetDraft.value)
    : 0,
);
const runFalsePositives = computed(() =>
  selectedRun.value
    ? summarizeRunFalsePositives(selectedRun.value.run, selectedRun.value.cases)
    : null,
);
const datasetSaveError = computed(() => {
  if (saveErrorCode.value === "capture_token_expired") {
    return `${datasetError.value ?? "The capture token expired."} Re-resolve the selected PRs; your draft is still open.`;
  }
  if (saveErrorCode.value === "version_conflict") {
    return `${datasetError.value ?? "This dataset changed in another session."} Reload the dataset to compare versions; your draft is still open.`;
  }
  return datasetError.value;
});
const refreshingCaseId = ref<string | null>(null);

onMounted(() => {
  window.addEventListener("beforeunload", handleBeforeUnload);
  beginPolling();
  void store.loadAvailability();
});

async function loadRepositoryDatasets(repositoryId: string, enabled: boolean) {
  const requestId = ++repositoryLoadRequestId;
  const isCurrentRepository = () =>
    !workspaceUnmounted &&
    requestId === repositoryLoadRequestId &&
    repositoryId === props.workspaceRepository.id;
  if (!isCurrentRepository()) return;
  if (!canLeaveDatasetWorkspace()) {
    store.invalidateDatasetLoad();
    repositoryLoadDeferred = true;
    return;
  }
  repositoryLoadDeferred = false;
  inboxIntentReady = false;
  invalidateDatasetCaptureRequests();
  store.invalidateDatasetLoad(repositoryId);
  if (!isCurrentRepository()) return;
  const originalInboxIntentId = ++inboxIntentRequestId;
  datasetEditorOpen.value = false;
  selectedPullRequestNumbers.value = new Set();
  selectionSaveError.value = null;
  showCreateDataset.value = false;
  datasets.value = [];
  selectedDatasetId.value = null;
  selectedDataset.value = null;
  datasetDraft.value = null;
  selectedRun.value = null;
  runs.value = [];
  runNextCursorId.value = null;

  if (enabled) {
    const initialEditEpoch = datasetDraftEditEpoch.value;
    const canApplyDefault = () =>
      isCurrentRepository() &&
      originalInboxIntentId === inboxIntentRequestId &&
      datasetDraftEditEpoch.value === initialEditEpoch &&
      !datasetDraftDirty.value;
    const loadedDatasets = await store.loadDatasets(repositoryId);
    if (!isCurrentRepository()) return;
    const firstDatasetId = loadedDatasets[0]?.id;
    const hasLinkedDataset =
      route.query.repositoryId === repositoryId &&
      typeof route.query.datasetId === "string" &&
      Number.isInteger(Number(route.query.prNumber)) &&
      Number(route.query.prNumber) > 0 &&
      props.canManage &&
      loadedDatasets.some((dataset) => dataset.id === route.query.datasetId);
    // Inbox links take precedence over the repository's default selection.
    if (
      !hasLinkedDataset &&
      firstDatasetId &&
      canApplyDefault() &&
      selectedDatasetId.value === null
    ) {
      await selectDataset(firstDatasetId, canApplyDefault);
    }
  }
  if (!isCurrentRepository()) return;
  inboxIntentReady = true;
  await openInboxDatasetIntent();
}

// Load the latest repository when availability resolves.
watch(
  [() => props.workspaceRepository.id, () => availability.value?.enabled],
  ([repositoryId, enabled]) => {
    if (enabled !== undefined)
      void loadRepositoryDatasets(repositoryId, enabled);
  },
  { immediate: true },
);
watch(datasetDraftDirty, (dirty) => {
  const enabled = availability.value?.enabled;
  if (!dirty && repositoryLoadDeferred && enabled !== undefined) {
    void loadRepositoryDatasets(props.workspaceRepository.id, enabled);
  }
});

watch(
  () => [route.query.repositoryId, route.query.datasetId, route.query.prNumber],
  () => {
    if (inboxIntentReady) void openInboxDatasetIntent();
  },
);

onBeforeUnmount(() => {
  workspaceUnmounted = true;
  inboxIntentRequestId += 1;
  invalidateDatasetCaptureRequests();
  store.invalidateDatasetLoad(null);
  endPolling();
  window.removeEventListener("beforeunload", handleBeforeUnload);
});

function canLeaveDatasetWorkspace() {
  if (!datasetDraftDirty.value) return true;
  datasetEditorOpen.value = true;
  return false;
}

onBeforeRouteLeave(canLeaveDatasetWorkspace);
onBeforeRouteUpdate((to, from) => {
  // Removing the already handled inbox intent only cleans up the URL.
  const removingHandledIntent = to.fullPath === pendingInboxCleanupPath;
  const repositoryChanged = to.query.repositoryId !== from.query.repositoryId;
  const linkedCaseChanged =
    to.query.datasetId !== from.query.datasetId ||
    to.query.prNumber !== from.query.prNumber;
  if (
    to.params.orgId !== from.params.orgId ||
    to.params.agentId !== from.params.agentId ||
    (repositoryChanged && !removingHandledIntent) ||
    (linkedCaseChanged && !removingHandledIntent)
  ) {
    return canLeaveDatasetWorkspace();
  }
});

defineExpose({ canLeaveDatasetWorkspace, datasetDraftDirty });

watch(
  () => props.agentId,
  async (agentId) => {
    selectedRevisionId.value = null;
    if (!selectedReviewerId.value && agentId)
      selectedReviewerId.value = agentId;
    if (agentId) {
      await store.loadRevisions(agentId);
    }
  },
  { immediate: true },
);

function beginPolling() {
  endPolling();
  pollHandle = setInterval(() => {
    const dataset = selectedDataset.value;
    const currentSelectedRun = selectedRun.value;
    const hasActiveListRun = runs.value.some((run) => isActiveRun(run.state));
    const hasActiveSelectedRun = Boolean(
      currentSelectedRun && isActiveRun(currentSelectedRun.run.state),
    );
    if (
      !dataset ||
      document.hidden ||
      (!hasActiveListRun && !hasActiveSelectedRun)
    ) {
      return;
    }
    // Poll the loaded dataset, including while a dirty draft keeps its repository open.
    if (hasActiveListRun && !loadingRuns.value) {
      void store.loadRuns(dataset.repositoryId, dataset.id);
    }
    if (hasActiveSelectedRun && !loadingRun.value && currentSelectedRun) {
      void store.loadRun(dataset.repositoryId, currentSelectedRun.run.id);
    }
  }, 2000);
}

function handleBeforeUnload(event: BeforeUnloadEvent) {
  if (!datasetDraftDirty.value) return;
  event.preventDefault();
  event.returnValue = "";
}

function endPolling() {
  if (pollHandle) {
    clearInterval(pollHandle);
    pollHandle = null;
  }
}

async function selectDataset(datasetId: string, shouldApply?: () => boolean) {
  invalidateDatasetCaptureRequests();
  const repositoryId = props.workspaceRepository.id;
  const requestId = repositoryLoadRequestId;
  const canApplySelection = () =>
    !workspaceUnmounted &&
    requestId === repositoryLoadRequestId &&
    props.workspaceRepository.id === repositoryId &&
    shouldApply?.() !== false;
  const loaded = await store.loadDataset(
    repositoryId,
    datasetId,
    canApplySelection,
  );
  if (!loaded || !canApplySelection()) return;
  selectedDatasetId.value = datasetId;
  datasetDraft.value = createEvaluationDatasetDraft(loaded);
  selectedRun.value = null;
  await store.loadRuns(repositoryId, datasetId);
}

async function openDatasetEditor(focusPullRequestNumber: number | null = null) {
  referenceFocusPr.value = focusPullRequestNumber;
  if (!selectedDatasetId.value) {
    return;
  }
  invalidateDatasetCaptureRequests();
  // Reopening the editor must retain staged captures and reference edits.
  if (datasetDraft.value?.datasetId === selectedDatasetId.value) {
    datasetEditorOpen.value = true;
    await store.searchTargets(
      props.workspaceRepository.id,
      "",
      undefined,
      false,
      selectedDatasetId.value,
    );
    return;
  }
  const loaded = await store.loadDataset(
    props.workspaceRepository.id,
    selectedDatasetId.value,
  );
  if (loaded) {
    selectedReviewerId.value = props.agentId ?? props.reviewers[0]?.id ?? null;
    datasetDraft.value = createEvaluationDatasetDraft(loaded);
    datasetEditorOpen.value = true;
    resolutionResults.value = [];
    refreshCaptures.value = {};
    await store.searchTargets(
      props.workspaceRepository.id,
      "",
      undefined,
      false,
      selectedDatasetId.value ?? undefined,
    );
  }
}

async function openInboxDatasetIntent() {
  const requestId = ++inboxIntentRequestId;
  const repositoryId = route.query.repositoryId;
  const datasetId = route.query.datasetId;
  const routeOrganizationId = route.params.orgId;
  const prNumber = Number(route.query.prNumber);
  if (
    typeof repositoryId !== "string" ||
    typeof datasetId !== "string" ||
    !Number.isInteger(prNumber) ||
    prNumber <= 0
  ) {
    return;
  }
  if (repositoryId !== props.workspaceRepository.id) {
    showInboxIntentNotice(
      "The repository changed before the dataset could open. Select the repository and try again.",
    );
    clearInboxDatasetIntent();
    return;
  }
  if (
    typeof routeOrganizationId === "string" &&
    routeOrganizationId !== store.activeOrganizationId
  ) {
    showInboxIntentNotice(
      "The organization changed before the dataset could open. Select the organization and try again.",
    );
    clearInboxDatasetIntent();
    return;
  }
  if (!canLeaveDatasetWorkspace()) {
    showInboxIntentNotice(
      "Save or discard the open dataset draft before opening another dataset.",
    );
    clearInboxDatasetIntent();
    return;
  }
  if (!availability.value?.enabled || !props.canManage) {
    clearInboxDatasetIntent();
    return;
  }

  const originalDraft = datasetDraft.value;
  const originalSelectedDatasetId = selectedDatasetId.value;
  const initialEditEpoch = datasetDraftEditEpoch.value;
  const draftOrSelectionChanged = () =>
    datasetDraft.value !== originalDraft ||
    datasetDraftEditEpoch.value !== initialEditEpoch ||
    selectedDatasetId.value !== originalSelectedDatasetId ||
    datasetDraftDirty.value;
  const isIntentCurrent = () =>
    requestId === inboxIntentRequestId &&
    route.query.repositoryId === repositoryId &&
    route.query.datasetId === datasetId &&
    route.query.prNumber === String(prNumber) &&
    props.workspaceRepository.id === repositoryId &&
    datasetDraft.value === originalDraft &&
    datasetDraftEditEpoch.value === initialEditEpoch &&
    selectedDatasetId.value === originalSelectedDatasetId;
  const canApplyIntent = () => isIntentCurrent() && !datasetDraftDirty.value;

  await store.loadDatasets(repositoryId);
  if (!canApplyIntent()) {
    if (
      requestId === inboxIntentRequestId &&
      route.query.datasetId === datasetId
    ) {
      showInboxIntentNotice(
        "The current dataset draft or selection changed while the requested dataset was loading. It was kept; save or discard it before opening another dataset.",
      );
      datasetEditorOpen.value = true;
      clearInboxDatasetIntent();
    }
    return;
  }
  if (!datasets.value.some((item) => item.id === datasetId)) {
    showInboxIntentNotice(
      "This dataset is not available in the selected repository.",
    );
    clearInboxDatasetIntent();
    return;
  }

  const loaded = await store.loadDataset(
    repositoryId,
    datasetId,
    canApplyIntent,
  );
  if (!loaded || !isIntentCurrent()) {
    if (
      requestId === inboxIntentRequestId &&
      route.query.datasetId === datasetId
    ) {
      if (draftOrSelectionChanged()) {
        showInboxIntentNotice(
          "The current dataset draft or selection changed while the requested dataset was loading. It was kept; save or discard it before opening another dataset.",
        );
        datasetEditorOpen.value = true;
      } else {
        showInboxIntentNotice(
          "Could not load the requested dataset. Your current dataset draft and selection were kept.",
        );
      }
      clearInboxDatasetIntent();
    }
    return;
  }

  selectedDatasetId.value = datasetId;
  datasetDraft.value = createEvaluationDatasetDraft(loaded);
  selectedRun.value = null;
  referenceFocusPr.value = prNumber;
  void store.loadRuns(repositoryId, datasetId);
  await openDatasetEditor(prNumber);
  if (requestId === inboxIntentRequestId) clearInboxDatasetIntent();
}

function clearInboxDatasetIntent() {
  const cleanupPath = router.resolve({
    query: {
      ...route.query,
      repositoryId: undefined,
      datasetId: undefined,
      prNumber: undefined,
    },
  }).fullPath;
  pendingInboxCleanupPath = cleanupPath;
  void router.replace(cleanupPath).finally(() => {
    if (pendingInboxCleanupPath === cleanupPath) {
      pendingInboxCleanupPath = null;
    }
  });
}

function showInboxIntentNotice(description: string) {
  toast.add({
    title: "Dataset not opened",
    description,
    color: "warning",
    icon: "i-hugeicons-alert-02",
  });
}

function createDataset(name: string, description?: string) {
  void store
    .createDataset(props.workspaceRepository.id, name, description)
    .then((created) => {
      if (!created) {
        return;
      }
      invalidateDatasetCaptureRequests();
      selectedDatasetId.value = created.id;
      selectedReviewerId.value =
        props.agentId ?? props.reviewers[0]?.id ?? null;
      datasetDraft.value = createEvaluationDatasetDraft(created);
      datasetEditorOpen.value = true;
      showCreateDataset.value = false;
      resolutionResults.value = [];
      void store.searchTargets(
        props.workspaceRepository.id,
        "",
        undefined,
        false,
        created.id,
      );
      void store.loadRuns(props.workspaceRepository.id, created.id);
    });
}

async function saveDatasetDraft(closeAfterSuccess = false) {
  if (
    !datasetDraft.value ||
    savingDataset.value ||
    resolvingPrs.value ||
    refreshingCaseId.value
  ) {
    return;
  }
  selectionSaveError.value = null;
  const submittedSelection = [...selectedPullRequestNumbers.value];
  if (submittedSelection.length > 0) {
    if (!(await resolvePrs(submittedSelection.map(String)))) return;
    const staged = new Set(
      datasetDraft.value?.cases.map((item) => item.pullRequestNumber),
    );
    if (
      selectedPullRequestNumbers.value.size > 0 ||
      submittedSelection.some((number) => !staged.has(number))
    ) {
      selectionSaveError.value =
        "Some selected PRs could not be captured. Check the results in Add PRs, then retry or uncheck those PRs.";
      return;
    }
  }
  if (!datasetDraft.value) return;
  const stagedPullRequestNumbers = new Set(
    datasetDraft.value.cases
      .filter((item) => item.captureToken)
      .map((item) => item.pullRequestNumber),
  );
  const saved = await store.saveDataset(
    props.workspaceRepository.id,
    datasetDraft.value,
  );
  if (saved) {
    invalidateDatasetCaptureRequests();
    const recapturedCaseIds = new Set(
      saved.cases
        .filter((item) =>
          stagedPullRequestNumbers.has(Number(item.pullRequestNumber)),
        )
        .map((item) => item.id),
    );
    caseSnapshots.value = Object.fromEntries(
      Object.entries(caseSnapshots.value).filter(
        ([caseId]) => !recapturedCaseIds.has(caseId),
      ),
    );
    datasetDraft.value = createEvaluationDatasetDraft(saved);
    selectedDatasetId.value = saved.id;
    const savedNumbers = new Set(
      saved.cases.map((item) => Number(item.pullRequestNumber)),
    );
    targetRows.value = targetRows.value.map((item) => ({
      ...item,
      alreadyInDataset: savedNumbers.has(Number(item.pullRequestNumber)),
    }));
    resolutionResults.value = [];
    refreshCaptures.value = {};
    if (closeAfterSuccess) closeDatasetEditor();
    const currentCaseId = historicalScope.value.caseId;
    if (
      !closeAfterSuccess &&
      currentCaseId &&
      recapturedCaseIds.has(currentCaseId)
    ) {
      await loadCaseSnapshot(currentCaseId);
    }
  }
}

async function archiveDataset() {
  const datasetId = selectedDatasetId.value;
  if (!datasetId) return;
  const archived = await store.archiveDataset(
    props.workspaceRepository.id,
    datasetId,
  );
  if (!archived) return;
  closeDatasetEditor();
  datasetDraft.value = null;
  selectedDatasetId.value = datasets.value[0]?.id ?? null;
  if (selectedDatasetId.value) await selectDataset(selectedDatasetId.value);
  else {
    runs.value = [];
    selectedRun.value = null;
  }
}

function startNewDataset() {
  closeDatasetEditor();
  datasetDraft.value = null;
  showCreateDataset.value = true;
}

function closeDatasetEditor() {
  invalidateDatasetCaptureRequests();
  selectedPullRequestNumbers.value = new Set();
  selectionSaveError.value = null;
  datasetEditorOpen.value = false;
}

function invalidateDatasetCaptureRequests() {
  store.invalidateDatasetCaptureRequests();
  refreshUiRequestId += 1;
  refreshingCaseId.value = null;
}

async function loadCaseSnapshot(caseId: string) {
  const datasetId = selectedDatasetId.value;
  if (!datasetId || !caseId) return;
  await store.loadCaseSnapshot(props.workspaceRepository.id, datasetId, caseId);
}

async function loadHistoricalFindings(caseId: string) {
  const orgId = store.activeOrganizationId;
  const evaluationCase = datasetDraft.value?.cases.find(
    (item) => item.caseId === caseId,
  );
  const reviewerId = selectedReviewerId.value;
  const reviewer = props.reviewers.find((item) => item.id === reviewerId);
  if (!orgId || !evaluationCase || !reviewer) return;
  const requestId = ++historicalRequestId;
  const scope = {
    caseId,
    pullRequestNumber: evaluationCase.pullRequestNumber,
    reviewerId,
  };
  historicalScope.value = scope;
  loadingHistorical.value = true;
  historicalLookupComplete.value = false;
  historicalFindings.value = [];
  const isCurrentScope = () =>
    requestId === historicalRequestId &&
    historicalScope.value.caseId === scope.caseId &&
    historicalScope.value.pullRequestNumber === scope.pullRequestNumber &&
    historicalScope.value.reviewerId === scope.reviewerId;
  try {
    const pullRequest = await CodeReviews.getPullRequestByNumber(orgId, {
      repositoryId: props.workspaceRepository.id,
      pullRequestNumber: evaluationCase.pullRequestNumber,
    });
    const reviews = await CodeReviews.listPullRequestCodeReviews(
      pullRequest.pullRequest.id,
      orgId,
      {
        createdAtUtc: pullRequest.pullRequest.createdAtUtc,
        pageSize: 20,
      },
    );
    const reviewResults = await Promise.all(
      reviews.items.map(async (review) => ({
        review,
        findings: await CodeReviews.getCodeReviewFindings(review.id, orgId, {
          createdAtUtc: review.createdAtUtc,
        }),
      })),
    );
    const imported = reviewResults.flatMap(({ review, findings }) =>
      findings.reviews
        .filter(
          (item) =>
            item.agent.toLocaleLowerCase() ===
            reviewer.displayName.toLocaleLowerCase(),
        )
        .flatMap((item) =>
          item.findings.map((finding, index) => ({
            id: `${review.id}:${index}`,
            title: finding.summary.slice(0, 300),
            summary: finding.body.slice(0, 4000),
            severity: referenceSeverity(finding.level),
            file: finding.file,
            line: finding.line === null ? null : Number(finding.line),
            origin: "HistoricalReview" as const,
            sourceRunId: null,
            sourceFindingId: `${review.id}:${index}`,
          })),
        ),
    );
    if (isCurrentScope()) historicalFindings.value = imported;
  } catch (error) {
    if (isCurrentScope()) {
      historicalFindings.value = [];
      datasetError.value =
        error instanceof Error
          ? error.message
          : "Could not load historical findings.";
    }
  } finally {
    if (isCurrentScope()) {
      loadingHistorical.value = false;
      historicalLookupComplete.value = true;
    }
  }
}

function setHistoricalScope(
  caseId: string | null,
  pullRequestNumber: number | null,
) {
  const nextScope = {
    caseId,
    pullRequestNumber,
    reviewerId: selectedReviewerId.value,
  };
  if (
    historicalScope.value.caseId === nextScope.caseId &&
    historicalScope.value.pullRequestNumber === nextScope.pullRequestNumber &&
    historicalScope.value.reviewerId === nextScope.reviewerId
  )
    return;
  historicalRequestId += 1;
  historicalScope.value = nextScope;
  historicalFindings.value = [];
  loadingHistorical.value = false;
  historicalLookupComplete.value = false;
}

function selectReviewer(reviewerId: string) {
  if (selectedReviewerId.value === reviewerId) return;
  selectedReviewerId.value = reviewerId;
  historicalRequestId += 1;
  historicalScope.value = {
    ...historicalScope.value,
    reviewerId,
  };
  historicalFindings.value = [];
  loadingHistorical.value = false;
  historicalLookupComplete.value = false;
}

function referenceSeverity(
  value: CodeReviewEvaluationFindingSeverity,
): CodeReviewEvaluationFindingSeverity {
  if (
    value === "Critical" ||
    value === "Major" ||
    value === "Minor" ||
    value === "Suggestion" ||
    value === "Comment"
  )
    return value;
  return "Comment";
}

function searchTargets(query: string) {
  void store.searchTargets(
    props.workspaceRepository.id,
    query,
    undefined,
    false,
    selectedDatasetId.value ?? undefined,
  );
}

function loadMoreTargets() {
  if (targetNextCursor.value) {
    void store.searchTargets(
      props.workspaceRepository.id,
      "",
      targetNextCursor.value,
      true,
      selectedDatasetId.value ?? undefined,
    );
  }
}

async function resolvePrs(entries: string[]) {
  const original = datasetDraft.value;
  if (!original || !datasetEditorOpen.value) return false;
  const sessionId = datasetEditorSessionId;
  const repositoryId = props.workspaceRepository.id;
  selectionSaveError.value = null;
  const items = await store.resolvePrs(
    repositoryId,
    original.datasetId,
    original.expectedVersion,
    entries,
  );
  const current = datasetDraft.value;
  const dataset = selectedDataset.value;
  if (
    items.length === 0 ||
    sessionId !== datasetEditorSessionId ||
    !current ||
    !dataset ||
    !datasetEditorOpen.value ||
    props.workspaceRepository.id !== repositoryId ||
    dataset.id !== original.datasetId ||
    current.datasetId !== original.datasetId ||
    current.expectedVersion !== original.expectedVersion
  )
    return false;

  let updated = current;
  const pending = new Set(selectedPullRequestNumbers.value);
  for (const item of items) {
    if (!item.captureToken || !item.preview) continue;
    const pullRequestNumber = Number(item.preview.pullRequestNumber);
    updated = addCapturedEvaluationCase(
      updated,
      {
        pullRequestNumber,
        title: item.target?.title ?? item.preview.title,
        captureToken: item.captureToken,
        preview: item.preview,
      },
      dataset.cases.find(
        (item) => Number(item.pullRequestNumber) === pullRequestNumber,
      ),
      props.reviewers.map((reviewer) => reviewer.id),
    );
    pending.delete(pullRequestNumber);
  }
  datasetDraft.value = updated;
  selectedPullRequestNumbers.value = pending;
  return true;
}

async function refreshCase(caseId: string) {
  const datasetId = selectedDatasetId.value;
  if (!datasetId) {
    return;
  }
  const requestId = ++refreshUiRequestId;
  refreshingCaseId.value = caseId;
  try {
    await store.refreshCase(props.workspaceRepository.id, datasetId, caseId);
  } finally {
    if (requestId === refreshUiRequestId) refreshingCaseId.value = null;
  }
}

function selectRevision(revisionId: string) {
  selectedRevisionId.value = revisionId || null;
  revisionDiffOpen.value = false;
  if (revisionId) void store.loadRevision(props.agentId ?? "", revisionId);
  else selectedRevision.value = null;
}

function openRevisionDiff() {
  if (selectedRevision.value) revisionDiffOpen.value = true;
}

async function runEvaluation() {
  if (
    !selectedDataset.value ||
    !props.agentId ||
    datasetDraftDirty.value ||
    selectedDataset.value.caseCount === 0
  ) {
    return;
  }
  const savedRevisionId = selectedRevisionId.value;
  const run = await store.createRun(props.workspaceRepository.id, {
    datasetId: selectedDataset.value.id,
    expectedVersion: Number(selectedDataset.value.version),
    agentId: props.agentId,
    savedRevisionId,
    draft: savedRevisionId ? null : props.reviewerConfiguration,
  });
  if (run) {
    selectedRun.value = await store.loadRun(
      props.workspaceRepository.id,
      run.id,
    );
  }
}

function backToLatest() {
  if (!canLeaveDatasetWorkspace()) return;
  selectedRun.value = null;
}

function viewRun(runId: string) {
  if (!canLeaveDatasetWorkspace()) return;
  void store.loadRun(props.workspaceRepository.id, runId);
}

function refreshHistory() {
  void store.loadRuns(
    props.workspaceRepository.id,
    selectedDatasetId.value ?? "",
  );
}

function loadMoreRuns() {
  if (runNextCursorId.value) {
    void store.loadRuns(
      props.workspaceRepository.id,
      selectedDatasetId.value ?? "",
      runNextCursorId.value,
      true,
    );
  }
}

function stageFinding(caseResult: EvaluationRunCaseDto, findingId: string) {
  if (savingDataset.value || !props.canManage) return;
  const candidate = caseResult.candidateFindings.find(
    (item) => item.id === findingId,
  );
  const sourceRunId = selectedRun.value?.run.id;
  const sourceDatasetId = selectedRun.value?.run.datasetId;
  if (!candidate || !sourceRunId || !sourceDatasetId) return;
  void (async () => {
    let draft: EvaluationDatasetDraft;
    if (datasetDraft.value?.datasetId === sourceDatasetId) {
      draft = datasetDraft.value;
    } else {
      if (datasetDraftDirty.value) {
        datasetEditorOpen.value = true;
        datasetError.value =
          "Save or discard the current dataset before adding a finding from another dataset.";
        return;
      }
      const repositoryId = props.workspaceRepository.id;
      const editEpoch = datasetDraftEditEpoch.value;
      const isCurrent = () =>
        !workspaceUnmounted &&
        props.workspaceRepository.id === repositoryId &&
        selectedRun.value?.run.id === sourceRunId &&
        datasetDraftEditEpoch.value === editEpoch &&
        !datasetDraftDirty.value;
      const loaded = await store.loadDataset(
        repositoryId,
        sourceDatasetId,
        isCurrent,
      );
      if (!loaded || !isCurrent()) return;
      draft = createEvaluationDatasetDraft(loaded);
      selectedDatasetId.value = loaded.id;
      await store.loadRuns(props.workspaceRepository.id, loaded.id);
    }
    const evaluationCase = draft.cases.find(
      (item) => item.caseId === caseResult.caseId,
    );
    if (
      !evaluationCase ||
      evaluationCase.captureToken ||
      evaluationCase.snapshot.baseSha !== caseResult.baseSha ||
      evaluationCase.snapshot.headSha !== caseResult.headSha
    ) {
      toast.add({
        title: "Finding not added",
        description:
          "This run used a different PR snapshot. Run the evaluation again on the current dataset.",
        color: "warning",
      });
      return;
    }
    const alreadyStaged = findStagedFindingDisposition(
      evaluationCase.referenceSets,
      caseResult.references.reviewerAgentId,
      sourceRunId,
      candidate.id,
    );
    if (alreadyStaged) {
      toast.add({
        title: "Finding already staged",
        description: `This claim is already marked ${alreadyStaged === "Rejected" ? "as a false positive" : "as expected"}.`,
        color: "neutral",
      });
      return;
    }
    selectedReviewerId.value = caseResult.references.reviewerAgentId;
    const reviewerId = caseResult.references.reviewerAgentId;
    const current = evaluationCase.referenceSets.find(
      (item) => item.reviewerAgentId === reviewerId,
    ) ?? {
      reviewerAgentId: reviewerId,
      state: "Expected" as const,
      findings: [],
    };
    const severity = toReferenceSeverity(candidate.severity);
    const reference = {
      id: crypto.randomUUID(),
      title: candidate.summary.slice(0, 300),
      summary: (candidate.details.trim() || candidate.summary).slice(0, 4000),
      severity,
      file: candidate.file,
      line: candidate.line === null ? null : Number(candidate.line),
      origin: "RunFinding" as const,
      sourceRunId,
      sourceFindingId: candidate.id,
      disposition: "Expected" as const,
      rationale: null,
    };
    draft = updateEvaluationCaseReferenceSet(draft, caseResult.caseId, {
      ...current,
      state: "Expected",
      findings: [...current.findings, reference],
    });
    datasetDraft.value = draft;
    referenceFocusPr.value = Number(caseResult.pullRequestNumber);
    datasetEditorOpen.value = true;
  })();
}

function discardDatasetDraft() {
  if (selectedDataset.value)
    datasetDraft.value = createEvaluationDatasetDraft(selectedDataset.value);
  selectedPullRequestNumbers.value = new Set();
  selectionSaveError.value = null;
}
function confirmDiscardDatasetDraft() {
  discardDatasetDraft();
  discardDraftConfirmOpen.value = false;
}

function openFalsePositiveModal(
  result: EvaluationRunCaseDto,
  findingId: string,
) {
  const candidate = result.candidateFindings.find(
    (item) => item.id === findingId,
  );
  const run = selectedRun.value?.run;
  if (!candidate || !run || savingDataset.value || !props.canManage) return;
  stagingError.value = null;
  pendingFalsePositive.value = {
    result,
    candidate,
    runId: run.id,
    datasetId: run.datasetId,
  };
  falsePositiveRationale.value = "";
  falsePositiveModalOpen.value = true;
}

async function submitFalsePositive() {
  const pending = pendingFalsePositive.value;
  if (
    !pending ||
    stagingFalsePositive.value ||
    savingDataset.value ||
    !props.canManage
  )
    return;
  stagingFalsePositive.value = true;
  try {
    let draft = datasetDraft.value;
    let loadedDatasetId: string | null = null;
    if (draft?.datasetId !== pending.datasetId) {
      if (datasetDraftDirty.value) {
        datasetEditorOpen.value = true;
        datasetError.value =
          "Save or discard the current dataset before adding a finding from another dataset.";
        return;
      }
      const repositoryId = props.workspaceRepository.id;
      const editEpoch = datasetDraftEditEpoch.value;
      const isCurrent = () =>
        !workspaceUnmounted &&
        props.workspaceRepository.id === repositoryId &&
        selectedRun.value?.run.id === pending.runId &&
        datasetDraftEditEpoch.value === editEpoch &&
        !datasetDraftDirty.value;
      const loaded = await store.loadDataset(
        repositoryId,
        pending.datasetId,
        isCurrent,
      );
      if (!loaded || !isCurrent()) {
        stagingError.value =
          "The dataset or run changed. Review the current draft and try again.";
        return;
      }
      draft = createEvaluationDatasetDraft(loaded);
      loadedDatasetId = loaded.id;
    }
    const evaluationCase = draft.cases.find(
      (item) => item.caseId === pending.result.caseId,
    );
    if (!evaluationCase) {
      stagingError.value = "This PR is no longer in the dataset.";
      return;
    }
    if (
      evaluationCase.snapshot.baseSha !== pending.result.baseSha ||
      evaluationCase.snapshot.headSha !== pending.result.headSha ||
      evaluationCase.captureToken
    ) {
      stagingError.value =
        "This run used a different PR snapshot. Run the evaluation again on the current dataset before adding this finding.";
      return;
    }
    const reviewerAgentId = pending.result.references.reviewerAgentId;
    const current = evaluationCase.referenceSets.find(
      (item) => item.reviewerAgentId === reviewerAgentId,
    ) ?? { reviewerAgentId, state: "Unlabelled" as const, findings: [] };
    if (
      current.findings.some(
        (item) =>
          item.sourceRunId === pending.runId &&
          item.sourceFindingId === pending.candidate.id,
      )
    ) {
      const existing = current.findings.find(
        (item) =>
          item.sourceRunId === pending.runId &&
          item.sourceFindingId === pending.candidate.id,
      );
      stagingError.value = `This finding is already marked ${existing?.disposition === "Rejected" ? "as a false positive" : "as expected"} in the dataset draft.`;
      return;
    }
    if (current.findings.length >= 100) {
      stagingError.value = "A reference set can hold at most 100 findings.";
      return;
    }
    const candidate = pending.candidate;
    const reference: CodeReviewEvaluationReferenceFindingDto = {
      id: crypto.randomUUID(),
      title: candidate.summary.slice(0, 300),
      summary: (candidate.details.trim() || candidate.summary).slice(0, 4000),
      severity: toReferenceSeverity(candidate.severity),
      file: candidate.file,
      line: candidate.line === null ? null : Number(candidate.line),
      origin: "RunFinding",
      sourceRunId: pending.runId,
      sourceFindingId: candidate.id,
      disposition: "Rejected",
      rationale: falsePositiveRationale.value.trim() || null,
    };
    datasetDraft.value = updateEvaluationCaseReferenceSet(
      draft,
      pending.result.caseId,
      { ...current, findings: [...current.findings, reference] },
    );
    if (loadedDatasetId) selectedDatasetId.value = loadedDatasetId;
    selectedReviewerId.value = reviewerAgentId;
    falsePositiveModalOpen.value = false;
  } finally {
    stagingFalsePositive.value = false;
  }
}

function toReferenceSeverity(value: CodeReviewEvaluationFindingSeverity) {
  if (
    value === "Critical" ||
    value === "Major" ||
    value === "Minor" ||
    value === "Suggestion" ||
    value === "Comment"
  )
    return value;
  return "Comment" as const;
}

function cancelRun(runId: string) {
  void store.cancelRun(props.workspaceRepository.id, runId);
}

function abandonRun(runId: string) {
  void store.abandonRun(props.workspaceRepository.id, runId);
}

function retryFailedRun(runId: string) {
  void store.retryFailedRun(props.workspaceRepository.id, runId);
}

function isActiveRun(state: EvaluationRunDto["state"]): boolean {
  return state === "Queued" || state === "Running";
}

function revisionCaption(run: EvaluationRunDto): string {
  if (!run.savedRevisionId) return "Draft captured for this run";
  const revision = revisions.value.find(
    (item) => item.id === run.savedRevisionId,
  );
  return revision?.label || `Saved revision ${run.savedRevisionId.slice(-8)}`;
}

function coverageLabel(run: EvaluationRunDto): string {
  const expected = Number(run.counts.expectedReferenceCount);
  const matched = Number(run.counts.matchedReferenceCount);
  return expected > 0
    ? `${Math.round((matched / expected) * 100)}% (${matched}/${expected})`
    : "N/A";
}

type PublicModelProvenance = {
  tier?: string;
  provider?: string;
  model?: string;
  providerModelVersion?: string | null;
};

function publicProvenance(json: string | null): PublicModelProvenance | null {
  if (!json) return null;
  try {
    const value: unknown = JSON.parse(json);
    if (!isRecord(value)) return null;
    return {
      tier: optionalString(value.Tier) ?? optionalString(value.tier),
      provider:
        optionalString(value.Provider) ?? optionalString(value.provider),
      model: optionalString(value.Model) ?? optionalString(value.model),
      providerModelVersion:
        optionalString(value.providerModelVersion) ??
        optionalString(value.ProviderModelVersion),
    };
  } catch {
    return null;
  }
}

function formatProvenance(value: PublicModelProvenance | null): string {
  if (!value) return "pending";
  const exactModel = value.providerModelVersion || value.model || "unavailable";
  return `${value.tier ?? "tier unavailable"} / ${value.provider ?? "provider unavailable"} / ${exactModel}`;
}

function provenanceCaption(cases: EvaluationRunCaseDto[]): string {
  const reviewer = cases.find(
    (item) => item.reviewerProvenanceJson,
  )?.reviewerProvenanceJson;
  const judge = cases.find(
    (item) => item.judgeProvenanceJson,
  )?.judgeProvenanceJson;
  return `Reviewer: ${formatProvenance(publicProvenance(reviewer ?? null))} · Judge: ${formatProvenance(publicProvenance(judge ?? null))}`;
}

function optionalString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

async function copyRunId(id: string) {
  await navigator.clipboard.writeText(id);
  useToast().add({ title: "Run ID copied", color: "success" });
}

function datasetName(datasetId: string): string {
  return (
    datasets.value.find((dataset) => dataset.id === datasetId)?.name ??
    "Archived dataset"
  );
}

function runStateColor(
  state: EvaluationRunDto["state"],
): "success" | "warning" | "error" | "neutral" {
  if (state === "Completed") return "success";
  if (state === "Incomplete") return "warning";
  if (
    state === "DispatchFailed" ||
    state === "Cancelled" ||
    state === "Abandoned"
  )
    return "error";
  return "neutral";
}

function formatDate(value: Date | string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value instanceof Date ? value : new Date(value));
}
</script>
