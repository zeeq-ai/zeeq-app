import { describe, expect, it } from "vitest";
import type {
  CodeReviewEvaluationCaseDto,
  CodeReviewEvaluationCapturePreviewDto,
  CodeReviewEvaluationDatasetDetailDto,
  CodeReviewEvaluationReferenceSetDto,
  EvaluationRunCaseDto,
} from "@/api/generated";
import {
  addCapturedEvaluationCase,
  buildEvaluationDatasetSaveRequest,
  countEvaluationDatasetDraftChanges,
  createEvaluationDatasetDraft,
  getEvaluationCaseGradeCounts,
  refreshEvaluationCase,
} from "./evaluation-view-models";

describe("evaluation dataset view models", () => {
  it("shows assessment counts only for graded cases and keeps clean zeroes", () => {
    const ungraded = runCase({ outcome: "JudgeFailed", phase: "Grading" });
    expect(getEvaluationCaseGradeCounts(ungraded)).toEqual({
      reference: "—",
      covered: "—",
      missed: "—",
      additional: "—",
    });

    const clean = runCase({
      outcome: "Graded",
      phase: "Graded",
      references: {
        reviewerAgentId: "agent-a",
        state: "ReviewedClean",
        findings: [],
      },
    });
    expect(getEvaluationCaseGradeCounts(clean)).toEqual({
      reference: 0,
      covered: 0,
      missed: 0,
      additional: 1,
    });
  });

  it("builds a complete desired membership without server-owned snapshot fields", () => {
    const draft = createEvaluationDatasetDraft(dataset());
    const next = addCapturedEvaluationCase(draft, {
      pullRequestNumber: 502,
      title: "Add search ranking",
      captureToken: "opaque-capture",
      preview: capturePreview({
        pullRequestNumber: 502,
        title: "Add search ranking",
      }),
    });

    const request = buildEvaluationDatasetSaveRequest(next);

    expect(request).toEqual({
      expectedVersion: 3,
      name: "Core regressions",
      description: "Common correctness examples",
      cases: [
        {
          caseId: "case-501",
          pullRequestNumber: 501,
          captureToken: null,
          referenceSets: [referenceSet()],
        },
        {
          caseId: null,
          pullRequestNumber: 502,
          captureToken: "opaque-capture",
          referenceSets: [],
        },
      ],
    });
    expect(request.cases[0]).not.toHaveProperty("snapshotSha256");
    expect(request.cases[0]).not.toHaveProperty("title");
  });

  it("reuses the soft-removed case id and clears old references on re-add", () => {
    const removed = caseDto({ removedAtUtc: new Date("2026-10-01T00:00:00Z") });
    const draft = createEvaluationDatasetDraft(dataset({ cases: [removed] }));

    const next = addCapturedEvaluationCase(
      draft,
      {
        pullRequestNumber: 501,
        title: "Updated title",
        captureToken: "fresh-capture",
        preview: capturePreview({
          pullRequestNumber: 501,
          title: "Updated title",
        }),
      },
      removed,
    );

    expect(next.cases).toHaveLength(1);
    expect(next.cases[0].caseId).toBe("case-501");
    expect(next.cases[0].referenceSets).toEqual([
      { reviewerAgentId: "agent-a", state: "Unlabelled", findings: [] },
    ]);
  });

  it("treats an active case re-add as a no-op", () => {
    const draft = createEvaluationDatasetDraft(dataset());

    const next = addCapturedEvaluationCase(draft, {
      pullRequestNumber: 501,
      title: "Changed provider title",
      captureToken: "unused-capture",
      preview: capturePreview({
        pullRequestNumber: 501,
        title: "Changed provider title",
      }),
    });

    expect(next).toBe(draft);
    expect(next.cases[0].title).toBe("Fix ordering regression");
  });

  it("refreshes a snapshot without carrying stale labels", () => {
    const draft = createEvaluationDatasetDraft(dataset());

    const next = refreshEvaluationCase(draft, "case-501", {
      captureToken: "fresh-capture",
      title: "Fix ordering with new inputs",
      preview: capturePreview({
        pullRequestNumber: 501,
        title: "Fix ordering with new inputs",
      }),
    });
    const request = buildEvaluationDatasetSaveRequest(next);

    expect(next.cases[0]).toMatchObject({
      caseId: "case-501",
      captureToken: "fresh-capture",
      referenceSets: [
        { reviewerAgentId: "agent-a", state: "Unlabelled", findings: [] },
      ],
    });
    expect(request.cases[0].pullRequestNumber).toBe(501);
  });

  it("counts additions, removals, refreshes, and reference edits once per case", () => {
    const draft = createEvaluationDatasetDraft(
      dataset({
        cases: [caseDto(), caseDto({ id: "case-502", pullRequestNumber: 502 })],
      }),
    );
    draft.cases.splice(0, 1);
    draft.cases.push({
      caseId: null,
      pullRequestNumber: 503,
      title: "New case",
      pullRequestRecordId: null,
      captureToken: "capture-503",
      snapshot: {
        title: "New case",
        baseSha: "base",
        headSha: "head",
        snapshotSha256: null,
        capturedAtUtc: new Date("2026-10-03T00:00:00Z"),
        changedFileCount: 1,
        omittedFileCount: 0,
      },
      referenceSets: [],
      baselineReferenceSets: [],
    });
    draft.cases[0].captureToken = "refresh-502";

    expect(countEvaluationDatasetDraftChanges(draft)).toBe(3);
  });
});

function runCase(
  overrides: Partial<EvaluationRunCaseDto> = {},
): EvaluationRunCaseDto {
  return {
    caseId: "case-1",
    pullRequestNumber: 901,
    title: "Sample result",
    baseSha: "base",
    headSha: "head",
    omittedFileCount: 0,
    references: { reviewerAgentId: "agent-a", state: "Expected", findings: [] },
    phase: "Graded",
    outcome: "Graded",
    candidateFindings: [
      {
        id: "candidate-1",
        summary: "One extra",
        details: "Details",
        file: "src/a.ts",
        line: 2,
        severity: "Major",
      },
    ],
    judgeMatches: [],
    reviewerProvenanceJson: null,
    judgeProvenanceJson: null,
    errorCode: null,
    ...overrides,
  };
}

function dataset(
  overrides: Partial<CodeReviewEvaluationDatasetDetailDto> = {},
): CodeReviewEvaluationDatasetDetailDto {
  return {
    id: "dataset-1",
    repositoryId: "repo-1",
    name: "Core regressions",
    description: "Common correctness examples",
    version: 3,
    caseCount: 1,
    createdAtUtc: new Date("2026-10-01T00:00:00Z"),
    updatedAtUtc: new Date("2026-10-02T00:00:00Z"),
    archivedAtUtc: null,
    cases: [caseDto()],
    ...overrides,
  };
}

function caseDto(
  overrides: Partial<CodeReviewEvaluationCaseDto> = {},
): CodeReviewEvaluationCaseDto {
  return {
    id: "case-501",
    pullRequestNumber: 501,
    title: "Fix ordering regression",
    pullRequestRecordId: "pull-501",
    baseSha: "base-sha",
    headSha: "head-sha",
    snapshotSha256: "sha256",
    capturedAtUtc: new Date("2026-10-02T00:00:00Z"),
    changedFileCount: 1,
    omittedFileCount: 0,
    removedAtUtc: null,
    referenceSets: [referenceSet()],
    ...overrides,
  };
}

function referenceSet(): CodeReviewEvaluationReferenceSetDto {
  return {
    reviewerAgentId: "agent-a",
    state: "Expected",
    findings: [
      {
        id: "finding-1",
        title: "Preserve ordering",
        summary: "Keep exact matches first.",
        severity: "Major",
        file: "src/Search.cs",
        line: 12,
        origin: "Manual",
        sourceRunId: null,
        sourceFindingId: null,
      },
    ],
  };
}

function capturePreview(
  overrides: Partial<CodeReviewEvaluationCapturePreviewDto> = {},
): CodeReviewEvaluationCapturePreviewDto {
  return {
    pullRequestNumber: 501,
    title: "Fix ordering regression",
    state: "Open",
    authorLogin: "octocat",
    baseSha: "base-sha",
    headSha: "head-sha",
    changedFileCount: 1,
    omittedFileCount: 0,
    capturedAtUtc: new Date("2026-10-02T00:00:00Z"),
    ...overrides,
  };
}
