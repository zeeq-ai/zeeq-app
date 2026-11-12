import { defineStore, acceptHMRUpdate } from "pinia";
import {
  CodeReviews,
  CodeReviewEvaluations,
  type CodeReviewEvaluationAvailabilityResponse,
  type CodeReviewEvaluationDatasetDetailDto,
  type CodeReviewEvaluationDatasetSummaryDto,
  type CodeReviewEvaluationSnapshot,
  type CodeReviewEvaluationPrResolutionDto,
  type CodeReviewEvaluationPrTargetDto,
  type CreateEvaluationRunRequest,
  type CodeReviewerAgentRevisionDto,
  type CodeReviewerAgentRevisionSummaryDto,
  type EvaluationRunDetailDto,
  type EvaluationRunDto,
  type RefreshCodeReviewEvaluationCaseMutationResponse,
} from "@/api/generated";
import { ZeeqApiError } from "@/api/zeeq-api-client";
import { useAppStore } from "@/stores/app-store";
import {
  buildEvaluationDatasetSaveRequest,
  type EvaluationDatasetDraft,
} from "@/views/code-reviews/manage-agents/evaluation-view-models";

/** Holds evaluation API state separately from ordinary reviewer tests. */
export const useCodeReviewEvaluationStore = defineStore(
  "code-review-evaluation-store",
  () => {
    const appStore = useAppStore();
    const activeOrganizationId = computed(
      () =>
        appStore.currentOrganization?.id ?? appStore.user?.organizationId ?? "",
    );

    const availability = ref<CodeReviewEvaluationAvailabilityResponse | null>(
      null,
    );
    const availabilityLoadedForOrganizationId = ref<string | null>(null);
    const loadingAvailability = ref(false);
    const availabilityError = ref<string | null>(null);
    const datasets = ref<CodeReviewEvaluationDatasetSummaryDto[]>([]);
    const selectedDataset = ref<CodeReviewEvaluationDatasetDetailDto | null>(
      null,
    );
    const targetRows = ref<CodeReviewEvaluationPrTargetDto[]>([]);
    const targetQuery = ref("");
    const targetNextCursor = ref<string | null>(null);
    const resolutionResults = ref<CodeReviewEvaluationPrResolutionDto[]>([]);
    const refreshCaptures = ref<
      Record<string, RefreshCodeReviewEvaluationCaseMutationResponse>
    >({});
    const caseSnapshots = ref<Record<string, CodeReviewEvaluationSnapshot>>({});
    const loadingSnapshotCaseId = ref<string | null>(null);
    const revisions = ref<CodeReviewerAgentRevisionSummaryDto[]>([]);
    const selectedRevision = ref<CodeReviewerAgentRevisionDto | null>(null);
    const runs = ref<EvaluationRunDto[]>([]);
    const selectedRun = ref<EvaluationRunDetailDto | null>(null);
    const runNextCursorId = ref<string | null>(null);
    const loadingDatasets = ref(false);
    const loadingDataset = ref(false);
    const creatingDataset = ref(false);
    const savingDataset = ref(false);
    const loadingTargets = ref(false);
    const loadingMoreTargets = ref(false);
    const resolvingPrs = ref(false);
    const loadingRevisions = ref(false);
    const loadingRuns = ref(false);
    const loadingRun = ref(false);
    const submittingRun = ref(false);
    const cancelingRun = ref(false);
    const retryingRun = ref(false);
    const datasetError = ref<string | null>(null);
    const targetError = ref<string | null>(null);
    const saveErrorCode = ref<string | null>(null);
    const createError = ref<string | null>(null);
    const runError = ref<string | null>(null);
    const runErrorCode = ref<string | null>(null);
    const runConflict = ref<string | null>(null);
    let pendingCreate: {
      name: string;
      description?: string;
      key: string;
    } | null = null;
    let pendingRunRequestKey: string | null = null;
    let availabilityRequestId = 0;

    /** Availability is org-scoped and controls only durable evaluation actions. */
    async function loadAvailability() {
      const orgId = activeOrganizationId.value;
      if (!orgId) {
        availability.value = null;
        availabilityLoadedForOrganizationId.value = null;
        availabilityError.value = null;
        return;
      }

      if (availabilityLoadedForOrganizationId.value === orgId) {
        return;
      }

      const requestId = ++availabilityRequestId;
      loadingAvailability.value = true;
      availabilityError.value = null;

      try {
        const response =
          await CodeReviews.getCodeReviewEvaluationAvailability(orgId);
        if (requestId !== availabilityRequestId) {
          return;
        }

        availability.value = response;
        availabilityLoadedForOrganizationId.value = orgId;
      } catch (err: unknown) {
        if (requestId !== availabilityRequestId) {
          return;
        }

        availability.value = null;
        availabilityLoadedForOrganizationId.value = null;
        availabilityError.value =
          err instanceof Error
            ? err.message
            : "Could not check reviewer evaluation availability.";
      } finally {
        if (requestId === availabilityRequestId) {
          loadingAvailability.value = false;
        }
      }
    }

    function clearAvailability() {
      availabilityRequestId += 1;
      availability.value = null;
      availabilityLoadedForOrganizationId.value = null;
      loadingAvailability.value = false;
      availabilityError.value = null;
    }

    async function loadDatasets(repositoryId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId) {
        datasets.value = [];
        selectedDataset.value = null;
        return;
      }

      loadingDatasets.value = true;
      datasetError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.listCodeReviewEvaluationDatasets(
            orgId,
            repositoryId,
          );
        datasets.value = response.items;
      } catch (err: unknown) {
        datasetError.value = evaluationErrorMessage(
          err,
          "Could not load datasets.",
        );
      } finally {
        loadingDatasets.value = false;
      }
    }

    async function loadDataset(repositoryId: string, datasetId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !datasetId) {
        selectedDataset.value = null;
        return null;
      }

      loadingDataset.value = true;
      datasetError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.getCodeReviewEvaluationDataset(
            orgId,
            repositoryId,
            datasetId,
          );
        selectedDataset.value = response.dataset;
        return response.dataset;
      } catch (err: unknown) {
        datasetError.value = evaluationErrorMessage(
          err,
          "Could not load this dataset.",
        );
        return null;
      } finally {
        loadingDataset.value = false;
      }
    }

    async function createDataset(
      repositoryId: string,
      name: string,
      description?: string,
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId) {
        return null;
      }

      const normalizedName = name.trim();
      const normalizedDescription = description?.trim() || undefined;
      if (
        !pendingCreate ||
        pendingCreate.name !== normalizedName ||
        pendingCreate.description !== normalizedDescription
      ) {
        pendingCreate = {
          name: normalizedName,
          description: normalizedDescription,
          key: crypto.randomUUID(),
        };
      }
      const createRequestKey = pendingCreate.key;

      creatingDataset.value = true;
      createError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.createCodeReviewEvaluationDataset(
            orgId,
            repositoryId,
            {
              name: normalizedName,
              description: normalizedDescription ?? null,
              createRequestKey,
            },
          );
        selectedDataset.value = response.dataset;
        pendingCreate = null;
        await loadDatasets(repositoryId);
        return response.dataset;
      } catch (err: unknown) {
        createError.value = evaluationErrorMessage(
          err,
          "Could not create the dataset.",
        );
        return null;
      } finally {
        creatingDataset.value = false;
      }
    }

    async function saveDataset(
      repositoryId: string,
      draft: EvaluationDatasetDraft,
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId) {
        return null;
      }

      savingDataset.value = true;
      datasetError.value = null;
      saveErrorCode.value = null;
      try {
        const response =
          await CodeReviewEvaluations.saveCodeReviewEvaluationDataset(
            orgId,
            repositoryId,
            draft.datasetId,
            buildEvaluationDatasetSaveRequest(draft),
          );
        selectedDataset.value = response.dataset;
        await loadDatasets(repositoryId);
        return response.dataset;
      } catch (err: unknown) {
        saveErrorCode.value = evaluationErrorCode(err);
        datasetError.value = evaluationErrorMessage(
          err,
          "Could not save the dataset. Your draft is still open.",
        );
        return null;
      } finally {
        savingDataset.value = false;
      }
    }

    async function archiveDataset(repositoryId: string, datasetId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !datasetId) return false;
      try {
        await CodeReviewEvaluations.archiveCodeReviewEvaluationDataset(
          orgId,
          repositoryId,
          datasetId,
        );
        await loadDatasets(repositoryId);
        selectedDataset.value = null;
        return true;
      } catch (err: unknown) {
        datasetError.value = evaluationErrorMessage(
          err,
          "Could not archive this dataset.",
        );
        return false;
      }
    }

    async function searchTargets(
      repositoryId: string,
      query: string,
      cursor?: string | null,
      append = false,
      datasetId?: string,
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId) {
        return;
      }

      if (append) {
        loadingMoreTargets.value = true;
      } else {
        loadingTargets.value = true;
        targetRows.value = [];
        targetQuery.value = query;
      }
      targetError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.listCodeReviewEvaluationPrTargets(
            orgId,
            repositoryId,
            {
              q: targetQuery.value || undefined,
              datasetId,
              cursor: cursor ?? undefined,
              pageSize: 50,
            },
          );
        targetRows.value = append
          ? [...targetRows.value, ...response.items]
          : response.items;
        targetNextCursor.value = response.nextCursor ?? null;
      } catch (err: unknown) {
        targetError.value = evaluationErrorMessage(
          err,
          "Could not load pull requests.",
        );
      } finally {
        loadingTargets.value = false;
        loadingMoreTargets.value = false;
      }
    }

    async function resolvePrs(
      repositoryId: string,
      datasetId: string,
      expectedDatasetVersion: number,
      entries: string[],
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !datasetId || entries.length === 0) {
        return [];
      }

      resolvingPrs.value = true;
      targetError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.resolveCodeReviewEvaluationPrTargets(
            orgId,
            repositoryId,
            { datasetId, expectedDatasetVersion, entries },
          );
        resolutionResults.value = response.items;
        return response.items;
      } catch (err: unknown) {
        targetError.value = evaluationErrorMessage(
          err,
          "Could not resolve pull requests.",
        );
        return [];
      } finally {
        resolvingPrs.value = false;
      }
    }

    async function refreshCase(
      repositoryId: string,
      datasetId: string,
      caseId: string,
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !datasetId || !caseId) {
        return null;
      }

      try {
        const response =
          await CodeReviewEvaluations.refreshCodeReviewEvaluationCase(
            orgId,
            repositoryId,
            datasetId,
            caseId,
          );
        refreshCaptures.value = {
          ...refreshCaptures.value,
          [caseId]: response,
        };
        return response;
      } catch (err: unknown) {
        targetError.value = evaluationErrorMessage(
          err,
          "Could not refresh this snapshot.",
        );
        return null;
      }
    }

    async function loadCaseSnapshot(
      repositoryId: string,
      datasetId: string,
      caseId: string,
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !datasetId || !caseId) return null;
      loadingSnapshotCaseId.value = caseId;
      targetError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.getCodeReviewEvaluationCaseSnapshot(
            orgId,
            repositoryId,
            datasetId,
            caseId,
          );
        caseSnapshots.value = {
          ...caseSnapshots.value,
          [caseId]: response.snapshot,
        };
        return response.snapshot;
      } catch (err: unknown) {
        targetError.value = evaluationErrorMessage(
          err,
          "Could not load the captured pull request diff.",
        );
        return null;
      } finally {
        if (loadingSnapshotCaseId.value === caseId)
          loadingSnapshotCaseId.value = null;
      }
    }

    async function loadRevisions(agentId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !agentId) {
        revisions.value = [];
        selectedRevision.value = null;
        return;
      }

      loadingRevisions.value = true;
      try {
        const response =
          await CodeReviewEvaluations.listCodeReviewerAgentRevisions(
            orgId,
            agentId,
          );
        revisions.value = response.items;
      } catch (err: unknown) {
        datasetError.value = evaluationErrorMessage(
          err,
          "Could not load reviewer revisions.",
        );
      } finally {
        loadingRevisions.value = false;
      }
    }

    async function loadRevision(agentId: string, revisionId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !agentId || !revisionId) {
        selectedRevision.value = null;
        return null;
      }

      try {
        const response =
          await CodeReviewEvaluations.getCodeReviewerAgentRevision(
            orgId,
            agentId,
            revisionId,
          );
        selectedRevision.value = response.revision;
        return response.revision;
      } catch (err: unknown) {
        datasetError.value = evaluationErrorMessage(
          err,
          "Could not load this reviewer revision.",
        );
        return null;
      }
    }

    async function loadRuns(
      repositoryId: string,
      datasetId: string,
      cursorId?: string | null,
      append = false,
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId) {
        runs.value = [];
        return;
      }

      loadingRuns.value = true;
      runError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.listCodeReviewEvaluationRuns(
            orgId,
            repositoryId,
            {
              datasetId: datasetId || undefined,
              cursorId: cursorId ?? undefined,
              pageSize: 25,
            },
          );
        runs.value = append
          ? [...runs.value, ...response.items]
          : response.items;
        runNextCursorId.value = response.nextCursorId ?? null;
      } catch (err: unknown) {
        runError.value = evaluationErrorMessage(
          err,
          "Could not load evaluation history.",
        );
      } finally {
        loadingRuns.value = false;
      }
    }

    async function loadRun(repositoryId: string, runId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !runId) {
        selectedRun.value = null;
        return null;
      }

      loadingRun.value = true;
      runError.value = null;
      try {
        const detail = await CodeReviewEvaluations.getCodeReviewEvaluationRun(
          orgId,
          repositoryId,
          runId,
        );
        selectedRun.value = detail;
        return detail;
      } catch (err: unknown) {
        runError.value = evaluationErrorMessage(
          err,
          "Could not load this evaluation.",
        );
        return null;
      } finally {
        loadingRun.value = false;
      }
    }

    async function createRun(
      repositoryId: string,
      request: Omit<CreateEvaluationRunRequest, "requestKey">,
    ) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId) {
        return null;
      }

      if (!pendingRunRequestKey) {
        pendingRunRequestKey = crypto.randomUUID();
      }
      const fullRequest: CreateEvaluationRunRequest = {
        ...request,
        requestKey: pendingRunRequestKey,
      };

      submittingRun.value = true;
      runError.value = null;
      runErrorCode.value = null;
      runConflict.value = null;
      try {
        const run = await CodeReviewEvaluations.createCodeReviewEvaluationRun(
          orgId,
          repositoryId,
          fullRequest,
        );
        pendingRunRequestKey = null;
        await Promise.all([
          loadRuns(repositoryId, request.datasetId),
          loadRun(repositoryId, run.id),
        ]);
        return run;
      } catch (err: unknown) {
        runErrorCode.value = evaluationErrorCode(err);
        runError.value = evaluationErrorMessage(
          err,
          "Could not start this evaluation.",
        );
        runConflict.value = evaluationConflictRunId(err);
        return null;
      } finally {
        submittingRun.value = false;
      }
    }

    async function cancelRun(repositoryId: string, runId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !runId) {
        return null;
      }

      cancelingRun.value = true;
      try {
        const run = await CodeReviewEvaluations.cancelCodeReviewEvaluationRun(
          orgId,
          repositoryId,
          runId,
        );
        await Promise.all([
          loadRuns(repositoryId, run.datasetId),
          loadRun(repositoryId, run.id),
        ]);
        return run;
      } catch (err: unknown) {
        runError.value = evaluationErrorMessage(
          err,
          "Could not cancel this evaluation.",
        );
        return null;
      } finally {
        cancelingRun.value = false;
      }
    }

    async function abandonRun(repositoryId: string, runId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !runId) {
        return null;
      }

      try {
        const run = await CodeReviewEvaluations.abandonCodeReviewEvaluationRun(
          orgId,
          repositoryId,
          runId,
        );
        await Promise.all([
          loadRuns(repositoryId, run.datasetId),
          loadRun(repositoryId, run.id),
        ]);
        return run;
      } catch (err: unknown) {
        runError.value = evaluationErrorMessage(
          err,
          "Could not abandon this queued evaluation.",
        );
        return null;
      }
    }

    async function retryFailedRun(repositoryId: string, runId: string) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !runId) {
        return null;
      }

      retryingRun.value = true;
      try {
        const run = await CodeReviewEvaluations.retryCodeReviewEvaluationRun(
          orgId,
          repositoryId,
          runId,
          { requestKey: crypto.randomUUID() },
        );
        await Promise.all([
          loadRuns(repositoryId, run.datasetId),
          loadRun(repositoryId, run.id),
        ]);
        return run;
      } catch (err: unknown) {
        runError.value = evaluationErrorMessage(
          err,
          "Could not retry failed cases.",
        );
        return null;
      } finally {
        retryingRun.value = false;
      }
    }

    return {
      activeOrganizationId,
      availability,
      availabilityLoadedForOrganizationId,
      availabilityError,
      loadingAvailability,
      datasets,
      selectedDataset,
      targetRows,
      targetQuery,
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
      loadingDatasets,
      loadingDataset,
      creatingDataset,
      savingDataset,
      loadingTargets,
      loadingMoreTargets,
      resolvingPrs,
      loadingRevisions,
      loadingRuns,
      loadingRun,
      submittingRun,
      cancelingRun,
      retryingRun,
      datasetError,
      targetError,
      saveErrorCode,
      createError,
      runError,
      runErrorCode,
      runConflict,
      loadAvailability,
      clearAvailability,
      loadDatasets,
      loadDataset,
      createDataset,
      saveDataset,
      archiveDataset,
      searchTargets,
      resolvePrs,
      refreshCase,
      loadCaseSnapshot,
      loadRevisions,
      loadRevision,
      loadRuns,
      loadRun,
      createRun,
      cancelRun,
      abandonRun,
      retryFailedRun,
    };
  },
);

function evaluationErrorCode(error: unknown): string | null {
  if (!(error instanceof ZeeqApiError) || !isRecord(error.data)) {
    return null;
  }
  return typeof error.data.code === "string" ? error.data.code : null;
}

function evaluationConflictRunId(error: unknown): string | null {
  if (!(error instanceof ZeeqApiError) || !isRecord(error.data)) {
    return null;
  }
  return typeof error.data.activeRunId === "string"
    ? error.data.activeRunId
    : null;
}

function evaluationErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof ZeeqApiError && isRecord(error.data)) {
    return typeof error.data.message === "string"
      ? error.data.message
      : fallback;
  }
  return error instanceof Error ? error.message : fallback;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useCodeReviewEvaluationStore, import.meta.hot),
  );
}
