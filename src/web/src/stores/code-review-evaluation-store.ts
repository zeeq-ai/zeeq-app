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
  type AddCodeReviewEvaluationCaseResponse,
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
    const inboxAddDatasets = ref<CodeReviewEvaluationDatasetSummaryDto[]>([]);
    const inboxAddDataset = ref<CodeReviewEvaluationDatasetDetailDto | null>(
      null,
    );
    const loadingInboxAddDatasets = ref(false);
    const loadingInboxAddDataset = ref(false);
    const addingInboxCase = ref(false);
    const creatingInboxDataset = ref(false);
    const inboxAddError = ref<string | null>(null);
    const inboxAddErrorCode = ref<string | null>(null);
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
      organizationId: string;
      repositoryId: string;
      name: string;
      description?: string;
      key: string;
    } | null = null;
    let pendingRunRequest: {
      organizationId: string;
      repositoryId: string;
      payload: string;
      key: string;
    } | null = null;
    let datasetLoadRequestId = 0;
    let targetRequestId = 0;
    let targetRequestScope: string | null = null;
    let resolutionRequestId = 0;
    let resolutionRequestScope: string | null = null;
    let refreshEpoch = 0;
    let refreshRequestId = 0;
    const refreshRequestIds = new Map<string, number>();
    let availabilityRequestId = 0;
    let inboxAddScope: string | null = null;
    let inboxAddListRequestId = 0;
    let inboxAddDetailRequestId = 0;
    let inboxAddMutationRequestId = 0;
    let inboxAddCreateRequestId = 0;
    let pendingInboxCreate: {
      organizationId: string;
      repositoryId: string;
      name: string;
      key: string;
    } | null = null;

    /** Bind picker requests to the currently visible PR/repository context. */
    function setInboxAddScope(scope: string | null) {
      if (inboxAddScope === scope) return;
      inboxAddScope = scope;
      inboxAddListRequestId += 1;
      inboxAddDetailRequestId += 1;
      inboxAddMutationRequestId += 1;
      inboxAddCreateRequestId += 1;
      loadingInboxAddDatasets.value = false;
      loadingInboxAddDataset.value = false;
      addingInboxCase.value = false;
      creatingInboxDataset.value = false;
      inboxAddError.value = null;
      inboxAddErrorCode.value = null;
      inboxAddDatasets.value = [];
      inboxAddDataset.value = null;
    }

    function invalidateInboxAddRequests() {
      setInboxAddScope(null);
      inboxAddDatasets.value = [];
    }

    async function loadInboxAddDatasets(repositoryId: string) {
      const orgId = activeOrganizationId.value;
      const scope = inboxAddScope;
      const requestId = ++inboxAddListRequestId;
      if (!orgId || !repositoryId || !scope) return [];
      loadingInboxAddDatasets.value = true;
      inboxAddError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.listCodeReviewEvaluationDatasets(
            orgId,
            repositoryId,
          );
        if (
          requestId !== inboxAddListRequestId ||
          inboxAddScope !== scope ||
          activeOrganizationId.value !== orgId
        ) {
          return [];
        }
        inboxAddDatasets.value = response.items;
        return response.items;
      } catch (err: unknown) {
        if (
          requestId === inboxAddListRequestId &&
          inboxAddScope === scope &&
          activeOrganizationId.value === orgId
        ) {
          inboxAddError.value = evaluationErrorMessage(
            err,
            "Could not load datasets.",
          );
        }
        return [];
      } finally {
        if (requestId === inboxAddListRequestId && inboxAddScope === scope) {
          loadingInboxAddDatasets.value = false;
        }
      }
    }

    async function loadInboxAddDataset(
      repositoryId: string,
      datasetId: string,
    ) {
      const orgId = activeOrganizationId.value;
      const scope = inboxAddScope;
      const requestId = ++inboxAddDetailRequestId;
      if (!orgId || !repositoryId || !datasetId || !scope) return null;
      loadingInboxAddDataset.value = true;
      inboxAddError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.getCodeReviewEvaluationDataset(
            orgId,
            repositoryId,
            datasetId,
          );
        if (
          requestId !== inboxAddDetailRequestId ||
          inboxAddScope !== scope ||
          activeOrganizationId.value !== orgId
        ) {
          return null;
        }
        inboxAddErrorCode.value = null;
        inboxAddError.value = null;
        inboxAddDataset.value = response.dataset;
        return response.dataset;
      } catch (err: unknown) {
        if (
          requestId === inboxAddDetailRequestId &&
          inboxAddScope === scope &&
          activeOrganizationId.value === orgId
        ) {
          inboxAddError.value = evaluationErrorMessage(
            err,
            "Could not load this dataset.",
          );
        }
        return null;
      } finally {
        if (requestId === inboxAddDetailRequestId && inboxAddScope === scope) {
          loadingInboxAddDataset.value = false;
        }
      }
    }

    async function addInboxPullRequest(
      repositoryId: string,
      datasetId: string,
      pullRequestNumber: number,
      expectedVersion: number,
    ): Promise<AddCodeReviewEvaluationCaseResponse | null> {
      const orgId = activeOrganizationId.value;
      const scope = inboxAddScope;
      const requestId = ++inboxAddMutationRequestId;
      if (!orgId || !repositoryId || !datasetId || !scope) return null;
      addingInboxCase.value = true;
      inboxAddError.value = null;
      inboxAddErrorCode.value = null;
      try {
        const response =
          await CodeReviewEvaluations.addCodeReviewEvaluationCase(
            orgId,
            repositoryId,
            datasetId,
            { pullRequestNumber, expectedVersion },
          );
        if (
          requestId !== inboxAddMutationRequestId ||
          inboxAddScope !== scope ||
          activeOrganizationId.value !== orgId
        ) {
          return null;
        }
        return response;
      } catch (err: unknown) {
        if (
          requestId === inboxAddMutationRequestId &&
          inboxAddScope === scope &&
          activeOrganizationId.value === orgId
        ) {
          inboxAddError.value =
            err instanceof ZeeqApiError && err.status < 500 && err.status !== 0
              ? evaluationErrorMessage(err, "Could not add this pull request.")
              : "Could not confirm whether this pull request was added. Retry to check.";
          inboxAddErrorCode.value = evaluationErrorCode(err);
        }
        return null;
      } finally {
        if (
          requestId === inboxAddMutationRequestId &&
          inboxAddScope === scope
        ) {
          addingInboxCase.value = false;
        }
      }
    }

    async function createInboxDataset(repositoryId: string, name: string) {
      const orgId = activeOrganizationId.value;
      const scope = inboxAddScope;
      const normalizedName = name.trim();
      const requestId = ++inboxAddCreateRequestId;
      if (!orgId || !repositoryId || !scope || !normalizedName) return null;
      if (
        !pendingInboxCreate ||
        pendingInboxCreate.organizationId !== orgId ||
        pendingInboxCreate.repositoryId !== repositoryId ||
        pendingInboxCreate.name !== normalizedName
      ) {
        pendingInboxCreate = {
          organizationId: orgId,
          repositoryId,
          name: normalizedName,
          key: crypto.randomUUID(),
        };
      }
      const createRequestKey = pendingInboxCreate.key;
      creatingInboxDataset.value = true;
      inboxAddError.value = null;
      try {
        const response =
          await CodeReviewEvaluations.createCodeReviewEvaluationDataset(
            orgId,
            repositoryId,
            { name: normalizedName, description: null, createRequestKey },
          );
        if (pendingInboxCreate?.key === createRequestKey) {
          pendingInboxCreate = null;
        }
        if (
          requestId !== inboxAddCreateRequestId ||
          inboxAddScope !== scope ||
          activeOrganizationId.value !== orgId
        ) {
          return null;
        }
        const created = response.dataset;
        const summary: CodeReviewEvaluationDatasetSummaryDto = {
          id: created.id,
          repositoryId: created.repositoryId,
          name: created.name,
          description: created.description,
          version: created.version,
          caseCount: created.caseCount,
          createdAtUtc: created.createdAtUtc,
          updatedAtUtc: created.updatedAtUtc,
          archivedAtUtc: created.archivedAtUtc,
        };
        // A list started before this create can contain an older membership set.
        inboxAddListRequestId += 1;
        loadingInboxAddDatasets.value = false;
        inboxAddDatasets.value = [
          summary,
          ...inboxAddDatasets.value.filter((item) => item.id !== created.id),
        ];
        inboxAddDataset.value = created;
        return created;
      } catch (err: unknown) {
        if (
          requestId === inboxAddCreateRequestId &&
          inboxAddScope === scope &&
          activeOrganizationId.value === orgId
        ) {
          inboxAddError.value = evaluationErrorMessage(
            err,
            "Could not create the dataset.",
          );
          inboxAddErrorCode.value = evaluationErrorCode(err);
        }
        return null;
      } finally {
        if (requestId === inboxAddCreateRequestId && inboxAddScope === scope) {
          creatingInboxDataset.value = false;
        }
      }
    }

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

    async function loadDataset(
      repositoryId: string,
      datasetId: string,
      shouldApply?: () => boolean,
    ) {
      const requestId = ++datasetLoadRequestId;
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId || !datasetId) {
        if (requestId === datasetLoadRequestId) {
          selectedDataset.value = null;
          loadingDataset.value = false;
        }
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
        if (
          requestId !== datasetLoadRequestId ||
          activeOrganizationId.value !== orgId ||
          shouldApply?.() === false
        ) {
          return null;
        }
        selectedDataset.value = response.dataset;
        return response.dataset;
      } catch (err: unknown) {
        if (
          requestId !== datasetLoadRequestId ||
          activeOrganizationId.value !== orgId ||
          shouldApply?.() === false
        ) {
          return null;
        }
        datasetError.value = evaluationErrorMessage(
          err,
          "Could not load this dataset.",
        );
        return null;
      } finally {
        if (requestId === datasetLoadRequestId) loadingDataset.value = false;
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
        pendingCreate.organizationId !== orgId ||
        pendingCreate.repositoryId !== repositoryId ||
        pendingCreate.name !== normalizedName ||
        pendingCreate.description !== normalizedDescription
      ) {
        pendingCreate = {
          organizationId: orgId,
          repositoryId,
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
        if (pendingCreate?.key === createRequestKey) pendingCreate = null;
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
      const requestId = ++targetRequestId;
      const orgId = activeOrganizationId.value;
      if (!orgId || !repositoryId) {
        targetRequestScope = null;
        targetRows.value = [];
        targetNextCursor.value = null;
        loadingTargets.value = false;
        loadingMoreTargets.value = false;
        return;
      }

      const requestQuery = append ? targetQuery.value : query;
      if (!append) {
        targetRows.value = [];
        targetNextCursor.value = null;
        targetQuery.value = query;
      }
      const requestScope = JSON.stringify([
        orgId,
        repositoryId,
        requestQuery,
        datasetId ?? null,
      ]);
      targetRequestScope = requestScope;
      loadingTargets.value = !append;
      loadingMoreTargets.value = append;
      targetError.value = null;
      const isCurrentRequest = () =>
        requestId === targetRequestId && targetRequestScope === requestScope;
      try {
        const response =
          await CodeReviewEvaluations.listCodeReviewEvaluationPrTargets(
            orgId,
            repositoryId,
            {
              q: requestQuery || undefined,
              datasetId,
              cursor: cursor ?? undefined,
              pageSize: 50,
            },
          );
        if (!isCurrentRequest() || activeOrganizationId.value !== orgId) {
          return;
        }
        targetRows.value = append
          ? [...targetRows.value, ...response.items]
          : response.items;
        targetNextCursor.value = response.nextCursor ?? null;
      } catch (err: unknown) {
        if (!isCurrentRequest() || activeOrganizationId.value !== orgId) {
          return;
        }
        targetError.value = evaluationErrorMessage(
          err,
          "Could not load pull requests.",
        );
      } finally {
        if (isCurrentRequest()) {
          loadingTargets.value = false;
          loadingMoreTargets.value = false;
        }
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

      const requestId = ++resolutionRequestId;
      const requestScope = JSON.stringify([
        orgId,
        repositoryId,
        datasetId,
        expectedDatasetVersion,
      ]);
      resolutionRequestScope = requestScope;
      resolvingPrs.value = true;
      targetError.value = null;
      resolutionResults.value = [];
      const isCurrentRequest = () =>
        requestId === resolutionRequestId &&
        resolutionRequestScope === requestScope;
      try {
        const response =
          await CodeReviewEvaluations.resolveCodeReviewEvaluationPrTargets(
            orgId,
            repositoryId,
            { datasetId, expectedDatasetVersion, entries },
          );
        if (!isCurrentRequest() || activeOrganizationId.value !== orgId) {
          return [];
        }
        resolutionResults.value = response.items;
        return response.items;
      } catch (err: unknown) {
        if (!isCurrentRequest() || activeOrganizationId.value !== orgId) {
          return [];
        }
        targetError.value = evaluationErrorMessage(
          err,
          "Could not resolve pull requests.",
        );
        return [];
      } finally {
        if (isCurrentRequest()) resolvingPrs.value = false;
      }
    }

    function invalidateDatasetCaptureRequests() {
      resolutionRequestId += 1;
      resolutionRequestScope = null;
      resolutionResults.value = [];
      resolvingPrs.value = false;
      refreshEpoch += 1;
      refreshRequestIds.clear();
      refreshCaptures.value = {};
      targetError.value = null;
    }

    function invalidateDatasetLoad() {
      datasetLoadRequestId += 1;
      loadingDataset.value = false;
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

      const requestEpoch = refreshEpoch;
      const requestId = ++refreshRequestId;
      const requestScope = JSON.stringify([
        orgId,
        repositoryId,
        datasetId,
        caseId,
      ]);
      refreshRequestIds.set(requestScope, requestId);
      const isCurrentRequest = () =>
        requestEpoch === refreshEpoch &&
        refreshRequestIds.get(requestScope) === requestId;
      try {
        const response =
          await CodeReviewEvaluations.refreshCodeReviewEvaluationCase(
            orgId,
            repositoryId,
            datasetId,
            caseId,
          );
        if (!isCurrentRequest() || activeOrganizationId.value !== orgId) {
          return null;
        }
        refreshCaptures.value = {
          ...refreshCaptures.value,
          [caseId]: response,
        };
        return response;
      } catch (err: unknown) {
        if (!isCurrentRequest() || activeOrganizationId.value !== orgId) {
          return null;
        }
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

      const payload = JSON.stringify(request);
      if (
        !pendingRunRequest ||
        pendingRunRequest.organizationId !== orgId ||
        pendingRunRequest.repositoryId !== repositoryId ||
        pendingRunRequest.payload !== payload
      ) {
        pendingRunRequest = {
          organizationId: orgId,
          repositoryId,
          payload,
          key: crypto.randomUUID(),
        };
      }
      const requestKey = pendingRunRequest.key;
      const fullRequest: CreateEvaluationRunRequest = {
        ...request,
        requestKey,
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
        if (pendingRunRequest?.key === requestKey) pendingRunRequest = null;
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
      inboxAddDatasets,
      inboxAddDataset,
      loadingInboxAddDatasets,
      loadingInboxAddDataset,
      addingInboxCase,
      creatingInboxDataset,
      inboxAddError,
      inboxAddErrorCode,
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
      setInboxAddScope,
      invalidateInboxAddRequests,
      loadInboxAddDatasets,
      loadInboxAddDataset,
      addInboxPullRequest,
      createInboxDataset,
      loadDataset,
      invalidateDatasetLoad,
      createDataset,
      saveDataset,
      archiveDataset,
      searchTargets,
      resolvePrs,
      invalidateDatasetCaptureRequests,
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
