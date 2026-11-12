<template>
  <div class="flex h-full min-h-0 flex-col overflow-hidden">
    <EvaluationToolbar
      v-if="availability?.enabled"
      :datasets="datasets"
      :selected-dataset-id="selectedDatasetId"
      :revisions="revisions"
      :selected-revision-id="selectedRevisionId"
      :disabled="
        !canManage || !agentId || datasetDraftDirty || loadingAvailability
      "
      :running="submittingRun"
      @select-dataset="selectDataset"
      @select-revision="selectRevision"
      @manage-datasets="openDatasetEditor"
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
        description="Quick test remains available. Contact your administrator if you need reusable datasets and run history."
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
        v-else-if="quickTestOpen"
        class="grid min-h-0 flex-1 grid-rows-[auto_minmax(0,1fr)] overflow-hidden"
      >
        <div
          class="flex items-center justify-between border-b border-default px-3 py-2"
        >
          <h3 class="text-sm font-semibold text-highlighted">
            Quick test · one PR · ephemeral
          </h3>
          <UButton
            label="Back to evaluation"
            color="neutral"
            variant="ghost"
            @click="quickTestOpen = false"
          />
        </div>
        <div
          class="grid min-h-0 gap-3 overflow-hidden p-3 lg:grid-cols-[minmax(18rem,0.8fr)_minmax(0,1.2fr)]"
        >
          <AgentTestPanel
            v-model:selected-pull-request="selectedQuickTestPullRequest"
            :targets="quickTestTargets"
            :loading="quickTestLoading"
            :loading-more="quickTestLoadingMore"
            :running="quickTestRunning"
            :disabled="!canManage || quickTestRunning"
            :has-more="quickTestHasMore"
            @load-targets="emits('loadQuickTestTargets')"
            @load-more-targets="emits('loadMoreQuickTestTargets')"
            @run="runQuickTest"
          />
          <AgentTestResultsPanel :result="quickTestResult" />
        </div>
      </div>

      <div v-else class="min-h-0 flex-1 overflow-y-auto p-3 sm:p-4">
        <div v-if="loadingAvailability" class="grid gap-3 p-4">
          <USkeleton class="h-10" />
          <USkeleton class="h-48" />
        </div>

        <div
          v-else-if="!availability?.enabled"
          class="flex h-full items-center justify-center"
        >
          <UEmpty
            icon="i-hugeicons-test-tube-01"
            title="Run a quick test"
            description="Use the single PR quick test to inspect the current draft without creating durable history."
          >
            <template #actions>
              <UButton
                label="Quick test"
                icon="i-hugeicons-play"
                color="neutral"
                variant="subtle"
                @click="openQuickTest"
              />
            </template>
          </UEmpty>
        </div>

        <div v-else-if="loadingDatasets" class="grid gap-3">
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
                @click="selectedRun = null"
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
            <span class="text-default"
              >Unlabelled
              <strong class="font-mono tabular-nums">{{
                selectedRun.run.counts.unlabelledCaseCount
              }}</strong></span
            >
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
            @add-to-reference="stageFinding"
          />

          <details class="border-t border-default pt-3">
            <summary
              class="cursor-pointer text-sm font-semibold text-highlighted"
            >
              Previous runs ({{ runs.length }})
            </summary>
            <div class="mt-3">
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
          </details>
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
            <div class="flex flex-wrap gap-2">
              <UButton
                label="Quick test"
                icon="i-hugeicons-play"
                color="neutral"
                variant="ghost"
                @click="openQuickTest"
              />
              <UButton
                label="Refresh history"
                icon="i-hugeicons-refresh"
                color="neutral"
                variant="ghost"
                :loading="loadingRuns"
                @click="refreshHistory"
              />
            </div>
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
      :dataset="selectedDataset"
      :repository-name="repositoryName"
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
      :save-error="datasetSaveError"
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
import { onBeforeRouteLeave } from "vue-router";
import type {
  CodeReviewAgentTestRunResponse,
  CodeReviewPullRequestDto,
  CodeReviewerAgentDto,
  CodeReviewerAgentRevisionConfiguration,
  CodeReviewEvaluationFindingSeverity,
  CodeReviewEvaluationReferenceFindingDto,
  EvaluationRunCaseDto,
  EvaluationRunDto,
} from "@/api/generated";
import { useCodeReviewEvaluationStore } from "@/stores/code-review-evaluation-store";
import { CodeReviews } from "@/api/generated";
import AgentTestPanel from "./AgentTestPanel.vue";
import AgentTestResultsPanel from "./AgentTestResultsPanel.vue";
import DatasetSlideover from "./DatasetSlideover.vue";
import EvaluationCreateDataset from "./EvaluationCreateDataset.vue";
import EvaluationRunCases from "./EvaluationRunCases.vue";
import EvaluationRunHistory from "./EvaluationRunHistory.vue";
import EvaluationRevisionDiff from "./EvaluationRevisionDiff.vue";
import EvaluationToolbar from "./EvaluationToolbar.vue";
import {
  countEvaluationDatasetDraftChanges,
  createEvaluationDatasetDraft,
  updateEvaluationCaseReferenceSet,
  type EvaluationDatasetDraft,
} from "./evaluation-view-models";

const props = defineProps<{
  repositoryId: string;
  repositoryName: string;
  agentId: string | null;
  reviewers: CodeReviewerAgentDto[];
  reviewerConfiguration: CodeReviewerAgentRevisionConfiguration;
  canManage: boolean;
  quickTestTargets: CodeReviewPullRequestDto[];
  quickTestLoading: boolean;
  quickTestLoadingMore: boolean;
  quickTestHasMore: boolean;
  quickTestRunning: boolean;
  quickTestResult: CodeReviewAgentTestRunResponse | null;
}>();

const emits = defineEmits<{
  loadQuickTestTargets: [];
  loadMoreQuickTestTargets: [];
  runQuickTest: [pullRequest: CodeReviewPullRequestDto];
}>();

const store = useCodeReviewEvaluationStore();
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
const datasetDraft = ref<EvaluationDatasetDraft | null>(null);
const datasetEditorOpen = ref(false);
const showCreateDataset = ref(false);
const quickTestOpen = ref(false);
const selectedQuickTestPullRequest = ref<CodeReviewPullRequestDto | null>(null);
let pollHandle: ReturnType<typeof setInterval> | null = null;

const datasetDraftDirty = computed(() => {
  if (!datasetDraft.value || !selectedDataset.value) {
    return false;
  }
  return (
    datasetDraft.value.name !== selectedDataset.value.name ||
    datasetDraft.value.description !==
      (selectedDataset.value.description ?? "") ||
    countEvaluationDatasetDraftChanges(datasetDraft.value) > 0
  );
});
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

onMounted(async () => {
  window.addEventListener("beforeunload", handleBeforeUnload);
  await store.loadAvailability();
  if (availability.value?.enabled) {
    await store.loadDatasets(props.repositoryId);
    selectedDatasetId.value = datasets.value[0]?.id ?? null;
    if (selectedDatasetId.value) {
      await selectDataset(selectedDatasetId.value);
    }
  }
  beginPolling();
});

onBeforeUnmount(() => {
  invalidateDatasetCaptureRequests();
  store.invalidateDatasetLoad();
  endPolling();
  window.removeEventListener("beforeunload", handleBeforeUnload);
});

onBeforeRouteLeave(() => {
  if (!datasetDraftDirty.value) return true;
  datasetEditorOpen.value = true;
  return false;
});

watch(
  () => props.repositoryId,
  async (repositoryId) => {
    if (datasetDraftDirty.value) {
      datasetEditorOpen.value = true;
      return;
    }
    invalidateDatasetCaptureRequests();
    store.invalidateDatasetLoad();
    selectedDatasetId.value = null;
    selectedDataset.value = null;
    datasetDraft.value = null;
    selectedRun.value = null;
    if (!availability.value?.enabled) return;
    await store.loadDatasets(repositoryId);
    selectedDatasetId.value = datasets.value[0]?.id ?? null;
    if (selectedDatasetId.value) {
      await selectDataset(selectedDatasetId.value);
    }
  },
);

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
    if (!document.hidden && runs.value.some((run) => isActiveRun(run.state))) {
      void store.loadRuns(props.repositoryId, selectedDatasetId.value ?? "");
      if (selectedRun.value && isActiveRun(selectedRun.value.run.state)) {
        void store.loadRun(props.repositoryId, selectedRun.value.run.id);
      }
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

async function selectDataset(datasetId: string) {
  invalidateDatasetCaptureRequests();
  selectedDatasetId.value = datasetId;
  const repositoryId = props.repositoryId;
  const loaded = await store.loadDataset(repositoryId, datasetId);
  if (
    !loaded ||
    selectedDatasetId.value !== datasetId ||
    props.repositoryId !== repositoryId
  ) {
    return;
  }
  datasetDraft.value = createEvaluationDatasetDraft(loaded);
  selectedRun.value = null;
  await store.loadRuns(repositoryId, datasetId);
}

async function openDatasetEditor() {
  referenceFocusPr.value = null;
  if (!selectedDatasetId.value) {
    return;
  }
  invalidateDatasetCaptureRequests();
  // Reopening the editor must retain staged captures and reference edits.
  if (datasetDraft.value?.datasetId === selectedDatasetId.value) {
    datasetEditorOpen.value = true;
    await store.searchTargets(
      props.repositoryId,
      "",
      undefined,
      false,
      selectedDatasetId.value,
    );
    return;
  }
  const loaded = await store.loadDataset(
    props.repositoryId,
    selectedDatasetId.value,
  );
  if (loaded) {
    selectedReviewerId.value = props.agentId ?? props.reviewers[0]?.id ?? null;
    datasetDraft.value = createEvaluationDatasetDraft(loaded);
    datasetEditorOpen.value = true;
    resolutionResults.value = [];
    refreshCaptures.value = {};
    await store.searchTargets(
      props.repositoryId,
      "",
      undefined,
      false,
      selectedDatasetId.value ?? undefined,
    );
  }
}

function createDataset(name: string, description?: string) {
  void store
    .createDataset(props.repositoryId, name, description)
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
        props.repositoryId,
        "",
        undefined,
        false,
        created.id,
      );
      void store.loadRuns(props.repositoryId, created.id);
    });
}

async function saveDatasetDraft(closeAfterSuccess = false) {
  if (!datasetDraft.value || resolvingPrs.value || refreshingCaseId.value) {
    return;
  }
  const stagedPullRequestNumbers = new Set(
    datasetDraft.value.cases
      .filter((item) => item.captureToken)
      .map((item) => item.pullRequestNumber),
  );
  const saved = await store.saveDataset(props.repositoryId, datasetDraft.value);
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
  const archived = await store.archiveDataset(props.repositoryId, datasetId);
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
  await store.loadCaseSnapshot(props.repositoryId, datasetId, caseId);
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
      repositoryId: props.repositoryId,
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
    props.repositoryId,
    query,
    undefined,
    false,
    selectedDatasetId.value ?? undefined,
  );
}

function loadMoreTargets() {
  if (targetNextCursor.value) {
    void store.searchTargets(
      props.repositoryId,
      "",
      targetNextCursor.value,
      true,
      selectedDatasetId.value ?? undefined,
    );
  }
}

function resolvePrs(entries: string[]) {
  if (!datasetDraft.value) {
    return;
  }
  void store.resolvePrs(
    props.repositoryId,
    datasetDraft.value.datasetId,
    datasetDraft.value.expectedVersion,
    entries,
  );
}

async function refreshCase(caseId: string) {
  const datasetId = selectedDatasetId.value;
  if (!datasetId) {
    return;
  }
  const requestId = ++refreshUiRequestId;
  refreshingCaseId.value = caseId;
  try {
    await store.refreshCase(props.repositoryId, datasetId, caseId);
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
  const run = await store.createRun(props.repositoryId, {
    datasetId: selectedDataset.value.id,
    expectedVersion: Number(selectedDataset.value.version),
    agentId: props.agentId,
    savedRevisionId,
    draft: savedRevisionId ? null : props.reviewerConfiguration,
  });
  if (run) {
    selectedRun.value = await store.loadRun(props.repositoryId, run.id);
  }
}

function viewRun(runId: string) {
  void store.loadRun(props.repositoryId, runId);
}

function refreshHistory() {
  void store.loadRuns(props.repositoryId, selectedDatasetId.value ?? "");
}

function loadMoreRuns() {
  if (runNextCursorId.value) {
    void store.loadRuns(
      props.repositoryId,
      selectedDatasetId.value ?? "",
      runNextCursorId.value,
      true,
    );
  }
}

function openQuickTest() {
  if (props.quickTestTargets.length === 0) emits("loadQuickTestTargets");
  quickTestOpen.value = true;
  selectedRun.value = null;
}

function runQuickTest(pullRequest: CodeReviewPullRequestDto) {
  emits("runQuickTest", pullRequest);
}

function stageFinding(caseResult: EvaluationRunCaseDto, findingId: string) {
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
      const loaded = await store.loadDataset(
        props.repositoryId,
        sourceDatasetId,
      );
      if (!loaded) return;
      draft = createEvaluationDatasetDraft(loaded);
      selectedDatasetId.value = loaded.id;
      await store.loadRuns(props.repositoryId, loaded.id);
    }
    const evaluationCase = draft.cases.find(
      (item) => item.caseId === caseResult.caseId,
    );
    if (!evaluationCase) return;
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
      summary: candidate.details.slice(0, 4000),
      severity,
      file: candidate.file,
      line: candidate.line === null ? null : Number(candidate.line),
      origin: "RunFinding" as const,
      sourceRunId,
      sourceFindingId: candidate.id,
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
  void store.cancelRun(props.repositoryId, runId);
}

function abandonRun(runId: string) {
  void store.abandonRun(props.repositoryId, runId);
}

function retryFailedRun(runId: string) {
  void store.retryFailedRun(props.repositoryId, runId);
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
