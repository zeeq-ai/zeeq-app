import { describe, expect, it } from "vitest";
import type {
  CodeReviewFindingDto,
  CodeReviewFindingTriageDto,
  CodeReviewFindingsResponse,
} from "@/api/generated";
import {
  patchFindingTriage,
  reconcileLoadedFindingTriages,
} from "./code-review-store";

describe("triage cache patch", () => {
  it("updates only the matching review and clears tombstones", () => {
    const finding = (): CodeReviewFindingDto => ({
      level: "Major",
      file: "a",
      line: null,
      side: null,
      summary: "s",
      body: "b",
      occurrenceKey: "F1",
      findingRef: "12345.F1",
      semanticId: "test-finding-one",
      triage: null,
    });
    const response = (id: string): CodeReviewFindingsResponse => ({
      codeReviewRecordId: id,
      codeReviewCreatedAtUtc: new Date(),
      noAgentsActivated: false,
      sourceTelemetry: null,
      reviews: [
        {
          facet: "f",
          agent: "a",
          summary: "",
          details: "",
          findings: [finding()],
        },
      ],
    });
    const cache = { one: response("one"), two: response("two") };
    const triage: CodeReviewFindingTriageDto = {
      id: "t",
      sourceReviewId: "one",
      sourceReviewCreatedAtUtc: new Date(),
      occurrenceKey: "F1",
      semanticId: "test-finding-one",
      facet: "f",
      severity: "Major",
      filePath: null,
      lineNumber: null,
      findingSummary: "s",
      disposition: "FalsePositive",
      approvalState: "Proposed",
      currentReason: "reason",
      channel: "Ui",
      sourceUrl: null,
      proposedByUserId: null,
      proposedAtUtc: null,
      confirmedByUserId: null,
      confirmedAtUtc: null,
      updatedAtUtc: new Date(),
    };
    const loadedTombstone = response("one");
    loadedTombstone.reviews[0]!.findings[0]!.triage = {
      ...triage,
      disposition: null,
      approvalState: "Open",
      currentReason: null,
    };
    const loadedCache = { one: loadedTombstone };
    const loadedVersions = new Map<string, bigint>();
    const loadedSnapshots = new Map<string, CodeReviewFindingTriageDto>();
    reconcileLoadedFindingTriages(
      loadedCache,
      loadedTombstone,
      loadedVersions,
      loadedSnapshots,
    );
    expect(loadedTombstone.reviews[0]!.findings[0]!.triage).toBeNull();
    expect(loadedSnapshots.get("one:F1")?.approvalState).toBe("Open");
    // A later load retains a real confirmation and still treats the other tombstone as open.
    const mixed = response("one");
    mixed.reviews[0]!.findings[0]!.triage = {
      ...triage,
      approvalState: "Confirmed",
      updatedAtUtc: new Date(triage.updatedAtUtc.getTime() + 1000),
    };
    mixed.reviews[0]!.findings.push({
      ...finding(),
      occurrenceKey: "F2",
      triage: {
        ...triage,
        occurrenceKey: "F2",
        approvalState: "Open",
        disposition: null,
      },
    });
    const mixedCache = { one: mixed };
    reconcileLoadedFindingTriages(
      mixedCache,
      mixed,
      loadedVersions,
      loadedSnapshots,
    );
    const rows = mixed.reviews[0]!.findings;
    expect(rows.filter((f) => !f.triage)).toHaveLength(1);
    expect(
      rows.filter((f) => f.triage?.approvalState === "Confirmed"),
    ).toHaveLength(1);
    const versions = new Map<string, bigint>();
    const snapshots = new Map<string, CodeReviewFindingTriageDto>();
    const emptyCache: Record<string, CodeReviewFindingsResponse> = {};
    patchFindingTriage(emptyCache, triage, versions, snapshots);
    expect(snapshots.size).toBe(1);
    emptyCache.one = response("one");
    for (const saved of snapshots.values())
      patchFindingTriage(emptyCache, saved, versions, snapshots);
    expect(emptyCache.one.reviews[0]!.findings[0]!.triage).toEqual(triage);
    patchFindingTriage(cache, triage, versions);
    expect(cache.one.reviews[0]!.findings[0]!.triage).toEqual(triage);
    expect(cache.two.reviews[0]!.findings[0]!.triage).toBeNull();
    patchFindingTriage(
      cache,
      {
        ...triage,
        approvalState: "Open",
        updatedAtUtc: new Date(triage.updatedAtUtc.getTime() + 1000),
      },
      versions,
    );
    expect(cache.one.reviews[0]!.findings[0]!.triage).toBeNull();
    patchFindingTriage(cache, triage, versions);
    expect(cache.one.reviews[0]!.findings[0]!.triage).toBeNull();
    const newer = {
      ...triage,
      updatedAtUtc: "2030-01-01T00:00:00.0000020Z" as unknown as Date,
    };
    const older = {
      ...triage,
      updatedAtUtc: "2030-01-01T00:00:00.0000010Z" as unknown as Date,
      currentReason: "stale",
    };
    patchFindingTriage(cache, newer, versions);
    patchFindingTriage(cache, older, versions);
    expect(cache.one.reviews[0]!.findings[0]!.triage?.currentReason).toBe(
      "reason",
    );
  });
});
