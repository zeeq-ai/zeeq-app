import type {
  CodeReviewEvaluationCaseDto,
  CodeReviewEvaluationCapturePreviewDto,
  CodeReviewEvaluationDatasetDetailDto,
  CodeReviewEvaluationReferenceSetDto,
  EvaluationRunCaseDto,
  SaveCodeReviewEvaluationDatasetRequest,
} from "@/api/generated";

/** Editable projection of one persisted membership or a newly captured PR. */
export type EvaluationCaseDraft = {
  caseId: string | null;
  pullRequestNumber: number;
  title: string;
  pullRequestRecordId: string | null;
  captureToken: string | null;
  snapshot: EvaluationSnapshotSummary;
  referenceSets: CodeReviewEvaluationReferenceSetDto[];
  baselineReferenceSets: CodeReviewEvaluationReferenceSetDto[];
};

export type EvaluationSnapshotSummary = {
  title: string;
  baseSha: string;
  headSha: string;
  snapshotSha256: string | null;
  capturedAtUtc: Date | string;
  changedFileCount: number;
  omittedFileCount: number;
};

/** Browser draft for one optimistic dataset Save. */
export type EvaluationDatasetDraft = {
  datasetId: string;
  expectedVersion: number;
  name: string;
  description: string;
  baselineCaseIds: string[];
  cases: EvaluationCaseDraft[];
};

export type EvaluationCaseGradeCounts = {
  reference: number | "—";
  covered: number | "—";
  missed: number | "—";
  additional: number | "—";
};

/** Returns assessment counts only for a case with a persisted binary grade. */
export function getEvaluationCaseGradeCounts(
  result: Pick<
    EvaluationRunCaseDto,
    "outcome" | "references" | "judgeMatches" | "candidateFindings"
  >,
): EvaluationCaseGradeCounts {
  if (result.outcome !== "Graded") {
    return { reference: "—", covered: "—", missed: "—", additional: "—" };
  }

  if (result.references.state === "Unlabelled") {
    return { reference: "—", covered: "—", missed: "—", additional: "—" };
  }

  const referenceCount =
    result.references.state === "Expected"
      ? result.references.findings.length
      : 0;
  const covered = result.judgeMatches.length;
  const matchedCandidateIds = new Set(
    result.judgeMatches.map((match) => match.candidateId),
  );
  return {
    reference: referenceCount,
    covered,
    missed: Math.max(0, referenceCount - covered),
    additional: result.candidateFindings.filter(
      (candidate) => !matchedCandidateIds.has(candidate.id),
    ).length,
  };
}

/** Creates independent editor state from the persisted active membership. */
export function createEvaluationDatasetDraft(
  dataset: CodeReviewEvaluationDatasetDetailDto,
): EvaluationDatasetDraft {
  return {
    datasetId: dataset.id,
    expectedVersion: Number(dataset.version),
    name: dataset.name,
    description: dataset.description ?? "",
    baselineCaseIds: dataset.cases
      .filter((evaluationCase) => !evaluationCase.removedAtUtc)
      .map((evaluationCase) => evaluationCase.id),
    cases: dataset.cases
      .filter((evaluationCase) => !evaluationCase.removedAtUtc)
      .map((evaluationCase) => {
        const referenceSets = cloneReferenceSets(evaluationCase.referenceSets);
        return {
          caseId: evaluationCase.id,
          pullRequestNumber: Number(evaluationCase.pullRequestNumber),
          title: evaluationCase.title,
          pullRequestRecordId: evaluationCase.pullRequestRecordId,
          captureToken: null,
          snapshot: {
            title: evaluationCase.title,
            baseSha: evaluationCase.baseSha,
            headSha: evaluationCase.headSha,
            snapshotSha256: evaluationCase.snapshotSha256,
            capturedAtUtc: evaluationCase.capturedAtUtc,
            changedFileCount: Number(evaluationCase.changedFileCount),
            omittedFileCount: Number(evaluationCase.omittedFileCount),
          },
          referenceSets,
          baselineReferenceSets: cloneReferenceSets(referenceSets),
        };
      }),
  };
}

/** Adds a resolved capture and reuses any soft-removed case identity. */
export function addCapturedEvaluationCase(
  draft: EvaluationDatasetDraft,
  capture: {
    pullRequestNumber: number;
    title: string;
    captureToken: string;
    preview: CodeReviewEvaluationCapturePreviewDto;
  },
  existingCase?: CodeReviewEvaluationCaseDto,
  reviewerAgentIds: string[] = [],
): EvaluationDatasetDraft {
  const activeIndex = draft.cases.findIndex(
    (item) => item.pullRequestNumber === capture.pullRequestNumber,
  );

  if (activeIndex >= 0) {
    return draft;
  }

  const existingReferenceSets = existingCase
    ? resetReferenceSets(existingCase.referenceSets)
    : reviewerAgentIds.map((reviewerAgentId) => ({
        reviewerAgentId,
        state: "Unlabelled" as const,
        findings: [],
      }));

  return {
    ...draft,
    cases: [
      ...draft.cases,
      {
        caseId: existingCase?.id ?? null,
        pullRequestNumber: capture.pullRequestNumber,
        title: capture.title,
        pullRequestRecordId: existingCase?.pullRequestRecordId ?? null,
        captureToken: capture.captureToken,
        snapshot: {
          title: capture.preview.title,
          baseSha: capture.preview.baseSha,
          headSha: capture.preview.headSha,
          snapshotSha256: null,
          capturedAtUtc: capture.preview.capturedAtUtc,
          changedFileCount: Number(capture.preview.changedFileCount),
          omittedFileCount: Number(capture.preview.omittedFileCount),
        },
        referenceSets: cloneReferenceSets(existingReferenceSets),
        baselineReferenceSets: cloneReferenceSets(existingReferenceSets),
      },
    ],
  };
}

/** Adds or replaces one reviewer scope without losing other staged scopes. */
export function updateEvaluationCaseReferenceSet(
  draft: EvaluationDatasetDraft,
  caseId: string,
  referenceSet: CodeReviewEvaluationReferenceSetDto,
): EvaluationDatasetDraft {
  return {
    ...draft,
    cases: draft.cases.map((evaluationCase) => {
      if (evaluationCase.caseId !== caseId) {
        return evaluationCase;
      }

      const referenceSets = evaluationCase.referenceSets.filter(
        (item) => item.reviewerAgentId !== referenceSet.reviewerAgentId,
      );
      referenceSets.push(referenceSet);
      referenceSets.sort((left, right) =>
        left.reviewerAgentId.localeCompare(right.reviewerAgentId),
      );
      return { ...evaluationCase, referenceSets };
    }),
  };
}

/** Removes membership from the draft; Save will soft-remove it on the server. */
export function removeEvaluationCase(
  draft: EvaluationDatasetDraft,
  caseId: string,
): EvaluationDatasetDraft {
  return {
    ...draft,
    cases: draft.cases.filter(
      (evaluationCase) => evaluationCase.caseId !== caseId,
    ),
  };
}

/** Replaces a case snapshot while keeping its identity and clearing stale labels. */
export function refreshEvaluationCase(
  draft: EvaluationDatasetDraft,
  caseId: string,
  capture: {
    captureToken: string;
    title: string;
    preview: CodeReviewEvaluationCapturePreviewDto;
  },
): EvaluationDatasetDraft {
  return {
    ...draft,
    cases: draft.cases.map((evaluationCase) => {
      if (evaluationCase.caseId !== caseId) {
        return evaluationCase;
      }

      const referenceSets = resetReferenceSets(evaluationCase.referenceSets);
      return {
        ...evaluationCase,
        title: capture.title,
        captureToken: capture.captureToken,
        snapshot: {
          title: capture.preview.title,
          baseSha: capture.preview.baseSha,
          headSha: capture.preview.headSha,
          snapshotSha256: null,
          capturedAtUtc: capture.preview.capturedAtUtc,
          changedFileCount: Number(capture.preview.changedFileCount),
          omittedFileCount: Number(capture.preview.omittedFileCount),
        },
        referenceSets,
        baselineReferenceSets: cloneReferenceSets(referenceSets),
      };
    }),
  };
}

/** Builds the complete desired membership without sending server-owned fields. */
export function buildEvaluationDatasetSaveRequest(
  draft: EvaluationDatasetDraft,
): SaveCodeReviewEvaluationDatasetRequest {
  return {
    expectedVersion: draft.expectedVersion,
    name: draft.name.trim(),
    description: draft.description.trim() || null,
    cases: draft.cases.map((evaluationCase) => ({
      caseId: evaluationCase.caseId,
      pullRequestNumber: evaluationCase.pullRequestNumber,
      captureToken: evaluationCase.captureToken,
      referenceSets: evaluationCase.referenceSets.map((referenceSet) => ({
        ...referenceSet,
        findings:
          referenceSet.state === "Expected"
            ? cloneReferenceSets([referenceSet])[0].findings
            : [],
      })),
    })),
  };
}

/** Counts changed draft values across membership and reference edits. */
export function countEvaluationDatasetDraftChanges(
  draft: EvaluationDatasetDraft,
): number {
  const added = draft.cases.filter((evaluationCase) => !evaluationCase.caseId);
  const currentCaseIds = new Set(
    draft.cases.flatMap((evaluationCase) =>
      evaluationCase.caseId ? [evaluationCase.caseId] : [],
    ),
  );
  const removed = draft.baselineCaseIds.filter(
    (caseId) => !currentCaseIds.has(caseId),
  );
  const removedOrEdited = draft.cases.filter((evaluationCase) => {
    if (!evaluationCase.caseId) {
      return false;
    }

    return (
      Boolean(evaluationCase.captureToken) ||
      !sameReferenceSets(
        evaluationCase.referenceSets,
        evaluationCase.baselineReferenceSets,
      )
    );
  });

  return added.length + removed.length + removedOrEdited.length;
}

function resetReferenceSets(
  referenceSets: CodeReviewEvaluationReferenceSetDto[],
): CodeReviewEvaluationReferenceSetDto[] {
  return referenceSets.map((referenceSet) => ({
    reviewerAgentId: referenceSet.reviewerAgentId,
    state: "Unlabelled",
    findings: [],
  }));
}

function cloneReferenceSets(
  referenceSets: CodeReviewEvaluationReferenceSetDto[],
): CodeReviewEvaluationReferenceSetDto[] {
  return referenceSets.map((referenceSet) => ({
    reviewerAgentId: referenceSet.reviewerAgentId,
    state: referenceSet.state,
    findings: referenceSet.findings.map((finding) => ({ ...finding })),
  }));
}

function sameReferenceSets(
  left: CodeReviewEvaluationReferenceSetDto[],
  right: CodeReviewEvaluationReferenceSetDto[],
): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}
