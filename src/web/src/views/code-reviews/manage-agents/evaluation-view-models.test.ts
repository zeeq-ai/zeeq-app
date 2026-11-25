import { describe, expect, it } from "vitest";
import type {
  CodeReviewEvaluationCaseDto,
  CodeReviewEvaluationCapturePreviewDto,
  CodeReviewEvaluationDatasetDetailDto,
  CodeReviewEvaluationReferenceSetDto,
  EvaluationRunCaseDto,
  EvaluationRunDto,
} from "@/api/generated";
import {
  addCapturedEvaluationCase,
  canAddEvaluationReferenceFinding,
  buildEvaluationDatasetSaveRequest,
  canRetryEvaluationRun,
  countEvaluationDatasetDraftChanges,
  countDatasetCaseReferences,
  summarizeRunFalsePositives,
  createEvaluationDatasetDraft,
  getEvaluationCaseGradeCounts,
  normalizeEvaluationFilePath,
  hasExpectedRejectedFileCollision,
  findStagedFindingDisposition,
  refreshEvaluationCase,
} from "./evaluation-view-models";

describe("evaluation dataset view models", () => {
  it("finds a staged source across dispositions and defaults legacy findings to expected", () => {
    const sets = [
      {
        ...referenceSet(),
        findings: [
          {
            ...referenceSet().findings[0],
            sourceRunId: "run-1",
            sourceFindingId: "candidate-1",
            disposition: "Rejected" as const,
          },
          {
            ...referenceSet().findings[0],
            id: "legacy",
            sourceRunId: "run-1",
            sourceFindingId: "candidate-2",
            disposition: undefined,
          },
        ],
      },
    ];
    expect(
      findStagedFindingDisposition(sets, "agent-a", "run-1", "candidate-1"),
    ).toBe("Rejected");
    expect(
      findStagedFindingDisposition(sets, "agent-a", "run-1", "candidate-2"),
    ).toBe("Expected");
    expect(
      findStagedFindingDisposition(sets, "agent-a", "run-2", "candidate-1"),
    ).toBeNull();
  });
  it("enforces the combined 100 finding cap", () => {
    const set = referenceSet();
    expect(canAddEvaluationReferenceFinding(set)).toBe(true);
    set.findings = Array.from({ length: 100 }, (_, index) => ({
      ...referenceSet().findings[0],
      id: `finding-${index}`,
      disposition:
        index % 2 === 0 ? ("Expected" as const) : ("Rejected" as const),
    }));
    expect(canAddEvaluationReferenceFinding(set)).toBe(false);
  });
  it("shows assessment counts only for graded cases and keeps clean zeroes", () => {
    const ungraded = runCase({ outcome: "JudgeFailed", phase: "Grading" });
    expect(getEvaluationCaseGradeCounts(ungraded)).toEqual({
      reference: "—",
      covered: "—",
      missed: "—",
      additional: "—",
      knownFalsePositives: "—",
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
      knownFalsePositives: "—",
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
    expect(next.cases[0].baselineReferenceSets).toEqual(
      draft.cases[0].baselineReferenceSets,
    );
    next.cases[0].captureToken = null;
    expect(countEvaluationDatasetDraftChanges(next)).toBe(1);
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
    repeatedFalsePositiveCount: 0,
    trackedFalsePositiveCount: 0,
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
        disposition: "Expected",
        rationale: null,
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

describe("evaluation recovery actions", () => {
  const counts = {
    caseCount: 3,
    gradedCaseCount: 1,
    skippedCaseCount: 0,
    failedCaseCount: 0,
    unlabelledCaseCount: 0,
    matchedReferenceCount: 0,
    missedReferenceCount: 0,
    expectedReferenceCount: 0,
    additionalFindingCount: 0,
  };
  it("offers failed-subset retry after explicit interruption recovery", () => {
    for (const state of [
      "Abandoned",
      "Cancelled",
      "DispatchFailed",
      "Incomplete",
    ] as const)
      expect(canRetryEvaluationRun({ state, counts })).toBe(true);
    for (const state of ["Queued", "Running", "Completed"] as const)
      expect(canRetryEvaluationRun({ state, counts })).toBe(false);
  });
  it("never retries successful, skipped or unlabelled cases", () => {
    expect(
      canRetryEvaluationRun({
        state: "Abandoned",
        counts: { ...counts, skippedCaseCount: 1, unlabelledCaseCount: 1 },
      }),
    ).toBe(false);
    expect(
      canRetryEvaluationRun({
        state: "Incomplete",
        counts: { ...counts, gradedCaseCount: 3 },
      }),
    ).toBe(false);
  });
});

describe("known false positive reporting", () => {
  const expected = {
    ...referenceSet().findings[0],
    approvedByUserId: "user",
    approvedAtUtc: new Date(),
    disposition: "Expected" as const,
  };
  const rejected = { ...expected, id: "fp", disposition: "Rejected" as const };
  const refs = {
    reviewerAgentId: "agent-a",
    state: "Expected" as const,
    findings: [expected, rejected],
  };
  const candidates = [
    {
      id: "e",
      summary: "expected",
      details: "details",
      file: "a",
      line: 1,
      severity: "Major" as const,
    },
    {
      id: "f",
      summary: "false",
      details: "details",
      file: "a",
      line: 2,
      severity: "Minor" as const,
    },
    {
      id: "extra",
      summary: "extra",
      details: "details",
      file: "a",
      line: 3,
      severity: "Minor" as const,
    },
  ];
  const matches = [
    {
      referenceId: expected.id,
      candidateId: "e",
      reason: "expected",
      disposition: "Expected" as const,
    },
    {
      referenceId: "fp",
      candidateId: "f",
      reason: "false",
      disposition: "Rejected" as const,
    },
  ];

  it.each(["Unlabelled", "ReviewedClean", "Expected"] as const)(
    "preserves rejected references when saving %s",
    (state) => {
      const draft = createEvaluationDatasetDraft(dataset());
      draft.cases[0].referenceSets[0] = {
        ...referenceSet(),
        state,
        findings: [
          referenceSet().findings[0],
          { ...referenceSet().findings[0], id: "fp", disposition: "Rejected" },
        ],
      };
      const findings =
        buildEvaluationDatasetSaveRequest(draft).cases[0].referenceSets![0]
          .findings;
      expect(findings.map((f) => f.id)).toEqual(
        state === "Expected" ? ["finding-1", "fp"] : ["fp"],
      );
      expect(
        countDatasetCaseReferences(draft.cases[0].referenceSets[0]),
      ).toEqual({ expectedCount: 1, rejectedCount: 1 });
    },
  );

  it("counts only expected references and matches for coverage", () => {
    expect(
      getEvaluationCaseGradeCounts(
        runCase({
          references: refs,
          candidateFindings: candidates,
          judgeMatches: matches,
          repeatedFalsePositiveCount: 1,
          trackedFalsePositiveCount: 1,
        }),
      ),
    ).toEqual({
      reference: 1,
      covered: 1,
      missed: 0,
      additional: 1,
      knownFalsePositives: { reported: 1, tracked: 1 },
    });
  });
  it.each([0, 1])(
    "reports false-positive-only cases with %i recurrence",
    (reported) => {
      const result = runCase({
        outcome: "FalsePositiveChecked",
        references: { ...refs, state: "Unlabelled", findings: [rejected] },
        candidateFindings: candidates,
        judgeMatches: reported ? [matches[1]] : [],
        repeatedFalsePositiveCount: reported,
        trackedFalsePositiveCount: 1,
      });
      expect(getEvaluationCaseGradeCounts(result)).toEqual({
        reference: "—",
        covered: "—",
        missed: "—",
        additional: reported ? 2 : 3,
        knownFalsePositives: { reported, tracked: 1 },
      });
    },
  );
  it.each([
    "Unlabelled",
    "None",
    "JudgeFailed",
    "ReviewFailed",
    "Skipped",
    "Cancelled",
  ] as const)("keeps %s ungraded", (outcome) => {
    const result = runCase({ outcome, references: refs });
    expect(getEvaluationCaseGradeCounts(result)).toEqual({
      reference: "—",
      covered: "—",
      missed: "—",
      additional: "—",
      knownFalsePositives: [
        "JudgeFailed",
        "ReviewFailed",
        "Skipped",
        "Cancelled",
      ].includes(outcome)
        ? "not-checked"
        : "—",
    });
    expect(
      getEvaluationCaseGradeCounts({
        ...result,
        references: { ...refs, findings: [expected] },
      }).knownFalsePositives,
    ).toBe("—");
  });
  it("does not subtract unchecked findings from backend checked counts", () => {
    const counts = {
      caseCount: 2,
      gradedCaseCount: 0,
      skippedCaseCount: 0,
      failedCaseCount: 1,
      unlabelledCaseCount: 0,
      matchedReferenceCount: 0,
      missedReferenceCount: 0,
      expectedReferenceCount: 0,
      additionalFindingCount: 0,
      falsePositiveCheckedCaseCount: 1,
      repeatedFalsePositiveCount: 1,
      trackedFalsePositiveCount: 2,
    };
    const run: EvaluationRunDto = {
      id: "r",
      datasetId: "d",
      datasetVersion: 1,
      agentId: "a",
      savedRevisionId: null,
      parentRunId: null,
      state: "Incomplete",
      createdAtUtc: new Date(),
      startedAtUtc: null,
      completedAtUtc: null,
      counts,
      errorCode: null,
    };
    expect(
      summarizeRunFalsePositives(run, [
        runCase({ outcome: "JudgeFailed", references: refs }),
        runCase({ outcome: "FalsePositiveChecked", references: refs }),
      ]),
    ).toEqual({
      reported: 1,
      checked: 2,
      unchecked: 1,
      onUnlabelledPrCount: 1,
    });
    expect(canRetryEvaluationRun(run)).toBe(true);
    expect(
      canRetryEvaluationRun({
        ...run,
        counts: { ...counts, caseCount: 1, failedCaseCount: 0 },
      }),
    ).toBe(false);
  });
});

describe("reference file collisions", () => {
  it("normalizes slash and prefix without removing a dotfile", () => {
    expect(normalizeEvaluationFilePath(" /./.github\\ci.yml ")).toBe(
      ".github/ci.yml",
    );
    expect(normalizeEvaluationFilePath("./.github/ci.yml")).toBe(
      ".github/ci.yml",
    );
    expect(normalizeEvaluationFilePath("github/ci.yml")).not.toBe(
      normalizeEvaluationFilePath(".github/ci.yml"),
    );
  });
  it("warns only for a nonempty same-file expected finding in Expected state", () => {
    const set = referenceSet();
    set.findings[0].file = ".github/ci.yml";
    expect(hasExpectedRejectedFileCollision(set, "./.github/ci.yml")).toBe(
      true,
    );
    expect(hasExpectedRejectedFileCollision(set, "github/ci.yml")).toBe(false);
    expect(hasExpectedRejectedFileCollision(set, "")).toBe(false);
    expect(
      hasExpectedRejectedFileCollision(
        { ...set, state: "Unlabelled" },
        ".github/ci.yml",
      ),
    ).toBe(false);
    expect(
      hasExpectedRejectedFileCollision(
        { ...set, findings: [{ ...set.findings[0], disposition: "Rejected" }] },
        ".github/ci.yml",
      ),
    ).toBe(false);
  });
});
