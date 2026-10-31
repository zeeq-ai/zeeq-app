import { describe, expect, it } from "vitest";
import {
  canCompareAuditSnapshotsInline,
  formatAuditTime,
  formatAuditTimelineDateTime,
  groupAuditFeedEntries,
  groupAuditEntriesByUtcDay,
  maxInlineAuditDiffCharacters,
  type AuditHistoryDocumentSnapshot,
  type AuditHistoryEntry,
} from "./audit-history-types";

function entry(
  id: string,
  occurredAtUtc: string,
  eventType = "document.updated",
  operationId: string | null = null,
  verifiedSyncRunId: string | null = null,
): AuditHistoryEntry {
  return {
    id,
    occurredAtUtc,
    actorLabel: "Alice",
    actorKind: "user",
    actorId: "alice-id",
    actorChannelLabel: "via Web",
    actionLabel: "edited",
    eventType,
    subjectLabel: "review.md",
    areaLabel: "Content",
    area: "Content",
    sourceLabel: "Library",
    type: "edited",
    actorIsSystemAdmin: false,
    partial: false,
    operationId,
    verifiedSyncRunId,
  };
}

describe("audit history display helpers", () => {
  it("allows inline comparison only within the combined character limit", () => {
    const snapshot = (content: string): AuditHistoryDocumentSnapshot => ({
      versionLabel: "Version",
      recordedAtUtc: "2026-10-31T01:05:00Z",
      fileName: "review.md",
      content,
    });

    expect(
      canCompareAuditSnapshotsInline(
        snapshot("a".repeat(maxInlineAuditDiffCharacters / 2)),
        snapshot("b".repeat(maxInlineAuditDiffCharacters / 2)),
      ),
    ).toBe(true);
    expect(
      canCompareAuditSnapshotsInline(
        snapshot("a".repeat(maxInlineAuditDiffCharacters / 2)),
        snapshot("b".repeat(maxInlineAuditDiffCharacters / 2 + 1)),
      ),
    ).toBe(false);
  });

  it("groups by UTC calendar day and preserves entry order", () => {
    const groups = groupAuditEntriesByUtcDay(
      [
        entry("newest", "2026-10-31T01:05:00Z"),
        entry("same-day", "2026-10-31T00:15:00Z"),
        entry("prior-day", "2026-10-30T23:55:00Z"),
      ],
      "en-US",
    );

    expect(groups.map((group) => group.key)).toEqual([
      "2026-10-31",
      "2026-10-30",
    ]);
    expect(groups[0]?.entries.map(({ id }) => id)).toEqual([
      "newest",
      "same-day",
    ]);
    expect(groups[1]?.entries.map(({ id }) => id)).toEqual(["prior-day"]);
    expect(groups[0]?.label).toContain("October 31, 2026");
  });

  it("returns a clear label for invalid event times", () => {
    expect(formatAuditTime("not-a-date", "en-US")).toBe("Unknown time");
    expect(formatAuditTimelineDateTime("not-a-date", "en-US")).toBe(
      "Unknown time",
    );
  });

  it("groups only loaded document sync facts with a verified run reference", () => {
    const operationId = "00112233-4455-6677-8899-aabbccddeeff";
    const verifiedRunId = "00112233445566778899aabbccddeeff";
    const items = groupAuditFeedEntries(
      [
        entry(
          "sync-a",
          "2026-10-31T01:05:00Z",
          "document.synced",
          operationId,
          verifiedRunId,
        ),
        entry("edit", "2026-10-31T01:04:00Z"),
        entry(
          "sync-b",
          "2026-10-31T01:03:00Z",
          "document.synced",
          operationId,
          verifiedRunId,
        ),
        entry(
          "sync-unverified",
          "2026-10-31T01:02:00Z",
          "document.synced",
          operationId,
        ),
      ],
      {},
    );

    expect(items.map((item) => item.kind)).toEqual([
      "sync-run",
      "event",
      "event",
    ]);
    expect(items[0]?.kind === "sync-run" && items[0].entries).toHaveLength(2);
    expect(items[0]?.kind === "sync-run" && items[0].operationState).toEqual({
      hasMore: true,
      loading: false,
      error: null,
    });
    expect(items.filter((item) => item.kind === "event")).toHaveLength(2);
  });

  it("keeps fallback sync facts separate when they share an ambient operation id", () => {
    const ambientOperationId = "00112233-4455-6677-8899-aabbccddeeff";
    const items = groupAuditFeedEntries(
      [
        entry(
          "external-delete-a",
          "2026-10-31T01:05:00Z",
          "document.synced",
          ambientOperationId,
        ),
        entry(
          "external-delete-b",
          "2026-10-31T01:04:00Z",
          "document.synced",
          ambientOperationId,
        ),
      ],
      {},
    );

    expect(items.map((item) => item.kind)).toEqual(["event", "event"]);
  });

  it("groups verified sync facts across libraries in the same run", () => {
    const operationId = "00112233-4455-6677-8899-aabbccddeeff";
    const verifiedRunId = "00112233445566778899aabbccddeeff";
    const first = entry(
      "library-a-sync",
      "2026-10-31T01:05:00Z",
      "document.synced",
      operationId,
      verifiedRunId,
    );
    const second = entry(
      "library-b-sync",
      "2026-10-31T01:04:00Z",
      "document.synced",
      operationId,
      verifiedRunId,
    );
    first.libraryId = "library-a";
    second.libraryId = "library-b";

    const items = groupAuditFeedEntries([first, second], {});

    expect(items).toHaveLength(1);
    expect(items[0]?.kind).toBe("sync-run");
    expect(items[0]?.kind === "sync-run" && items[0].entries).toHaveLength(2);
  });
});
