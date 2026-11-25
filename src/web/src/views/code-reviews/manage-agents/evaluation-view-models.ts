import type {
  CodeReviewEvaluationCaseDto,
  CodeReviewEvaluationCapturePreviewDto,
  CodeReviewEvaluationDatasetDetailDto,
  CodeReviewEvaluationReferenceSetDto,
  EvaluationRunCaseDto,
  EvaluationRunDto,
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
  knownFalsePositives:
    { reported: number; tracked: number } | "—" | "not-checked";
};

/** Returns assessment counts only for a case with a persisted binary grade. */
export function getEvaluationCaseGradeCounts(
  result: Pick<
    EvaluationRunCaseDto,
    | "outcome"
    | "references"
    | "judgeMatches"
    | "candidateFindings"
    | "trackedFalsePositiveCount"
    | "repeatedFalsePositiveCount"
  >,
): EvaluationCaseGradeCounts {
  const rejectedCount = result.references.findings.filter(
    (f) => f.disposition === "Rejected",
  ).length;
  const judged =
    result.outcome === "Graded" || result.outcome === "FalsePositiveChecked";
  const tracked = Number(result.trackedFalsePositiveCount ?? rejectedCount);
  const knownFalsePositives: EvaluationCaseGradeCounts["knownFalsePositives"] =
    judged
      ? tracked > 0
        ? { reported: Number(result.repeatedFalsePositiveCount ?? 0), tracked }
        : "—"
      : failedOutcome(result.outcome) && rejectedCount > 0
        ? "not-checked"
        : "—";
  if (!judged)
    return {
      reference: "—",
      covered: "—",
      missed: "—",
      additional: "—",
      knownFalsePositives,
    };
  if (result.outcome === "FalsePositiveChecked")
    return {
      reference: "—",
      covered: "—",
      missed: "—",
      additional: unmatchedCandidateCount(result),
      knownFalsePositives,
    };
  const reference =
    result.references.state === "Expected"
      ? result.references.findings.filter((f) => f.disposition !== "Rejected")
          .length
      : 0;
  const covered = result.judgeMatches.filter(
    (m) => m.disposition !== "Rejected",
  ).length;
  return {
    reference,
    covered,
    missed: Math.max(0, reference - covered),
    additional: unmatchedCandidateCount(result),
    knownFalsePositives,
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
        findings: cloneReferenceSets([referenceSet])[0].findings.filter(
          (finding) =>
            referenceSet.state === "Expected" ||
            finding.disposition === "Rejected",
        ),
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
    findings: referenceSet.findings.map((finding) => ({
      ...finding,
      disposition: finding.disposition ?? "Expected",
      rationale: finding.rationale ?? null,
    })),
  }));
}

function sameReferenceSets(
  left: CodeReviewEvaluationReferenceSetDto[],
  right: CodeReviewEvaluationReferenceSetDto[],
): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

/** Interrupted terminal runs can retry only cases without a successful final outcome. */
export function canRetryEvaluationRun(
  run: Pick<EvaluationRunDto, "state" | "counts">,
): boolean {
  if (
    !["Incomplete", "Abandoned", "Cancelled", "DispatchFailed"].includes(
      run.state,
    )
  )
    return false;
  return (
    Number(run.counts.caseCount) >
    Number(run.counts.gradedCaseCount) +
      Number(run.counts.skippedCaseCount) +
      Number(run.counts.unlabelledCaseCount) +
      Number(run.counts.falsePositiveCheckedCaseCount ?? 0)
  );
}

function unmatchedCandidateCount(
  result: Pick<EvaluationRunCaseDto, "judgeMatches" | "candidateFindings">,
): number {
  const matched = new Set(
    result.judgeMatches.map((match) => match.candidateId),
  );
  return result.candidateFindings.filter(
    (candidate) => !matched.has(candidate.id),
  ).length;
}
function failedOutcome(outcome: EvaluationRunCaseDto["outcome"]): boolean {
  return ["ReviewFailed", "JudgeFailed", "Skipped", "Cancelled"].includes(
    outcome,
  );
}
export function countDatasetCaseReferences(
  set: CodeReviewEvaluationReferenceSetDto,
) {
  return {
    expectedCount: set.findings.filter(
      (f) => (f.disposition ?? "Expected") === "Expected",
    ).length,
    rejectedCount: set.findings.filter((f) => f.disposition === "Rejected")
      .length,
  };
}
export function findStagedFindingDisposition(
  referenceSets: CodeReviewEvaluationReferenceSetDto[],
  reviewerAgentId: string,
  sourceRunId: string,
  sourceFindingId: string,
): "Expected" | "Rejected" | null {
  const finding = referenceSets
    .find((set) => set.reviewerAgentId === reviewerAgentId)
    ?.findings.find(
      (item) =>
        item.sourceRunId === sourceRunId &&
        item.sourceFindingId === sourceFindingId,
    );
  return finding ? (finding.disposition ?? "Expected") : null;
}
export function canAddEvaluationReferenceFinding(
  referenceSet: CodeReviewEvaluationReferenceSetDto,
): boolean {
  return referenceSet.findings.length < 100;
}
export function summarizeRunFalsePositives(
  run: EvaluationRunDto,
  cases: EvaluationRunCaseDto[],
) {
  const reported = Number(run.counts.repeatedFalsePositiveCount ?? 0);
  const checked = Number(run.counts.trackedFalsePositiveCount ?? 0);
  const unchecked = cases
    .filter((c) => failedOutcome(c.outcome))
    .reduce(
      (n, c) =>
        n +
        c.references.findings.filter((f) => f.disposition === "Rejected")
          .length,
      0,
    );
  return {
    reported,
    checked,
    unchecked,
    onUnlabelledPrCount: Number(run.counts.falsePositiveCheckedCaseCount ?? 0),
  };
}

/** Normalizes repository paths while preserving leading dotfiles. */
export function normalizeEvaluationFilePath(file: string): string {
  let path = file.trim().replaceAll("\\", "/");
  while (path.startsWith("/") || path.startsWith("./")) {
    path = path.startsWith("/") ? path.slice(1) : path.slice(2);
  }
  return path;
}

/** True when a rejected rule shares a file with an expected finding. */
export function hasExpectedRejectedFileCollision(
  set: CodeReviewEvaluationReferenceSetDto,
  rejectedFile: string,
): boolean {
  const file = normalizeEvaluationFilePath(rejectedFile);
  return (
    set.state === "Expected" &&
    file.length > 0 &&
    set.findings.some(
      (finding) =>
        finding.disposition !== "Rejected" &&
        normalizeEvaluationFilePath(finding.file) === file,
    )
  );
}
