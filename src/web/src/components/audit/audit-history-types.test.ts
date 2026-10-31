import { describe, expect, it } from "vitest";
import {
  canCompareAuditSnapshotsInline,
  formatAuditTime,
  formatAuditTimelineDateTime,
  formatAuditCoverage,
  getAuditHistoryGroupPage,
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
  subjectType = "document",
  subjectId = id,
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
    subjectType,
    subjectId,
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

  it("states current capture coverage without overstating source sync history", () => {
    expect(
      formatAuditCoverage(
        "partial: direct document changes, library lifecycle and repository-library mappings, public-source registration, organization administration, membership and activation keys; other configuration and external actions are not yet recorded.",
      ),
    ).toContain(
      "Public-source registration does not include historical synced document content.",
    );
    expect(formatAuditCoverage("unknown future coverage")).toBe(
      "unknown future coverage",
    );
  });

  it("pages large loaded groups in bounded windows and clamps page navigation", () => {
    const loadedChildren = Array.from({ length: 123 }, (_, index) => index + 1);
    const firstPage = getAuditHistoryGroupPage(loadedChildren, 0);
    const secondPage = getAuditHistoryGroupPage(loadedChildren, 1);
    const finalPage = getAuditHistoryGroupPage(loadedChildren, 2);
    const pastEnd = getAuditHistoryGroupPage(loadedChildren, 99, 200);

    expect(firstPage).toMatchObject({ start: 1, end: 50, total: 123 });
    expect(firstPage.items).toHaveLength(50);
    expect(secondPage).toMatchObject({ start: 51, end: 100, page: 1 });
    expect(secondPage.items).toHaveLength(50);
    expect(finalPage).toMatchObject({ start: 101, end: 123, page: 2 });
    expect(finalPage.items).toHaveLength(23);
    expect(pastEnd).toMatchObject({ start: 101, end: 123, page: 2 });
    expect(pastEnd.items).toHaveLength(23);
    expect(pastEnd.pageSize).toBe(50);
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

  it("groups deletion children only under a matching library deletion parent", () => {
    const operationId = "00112233-4455-6677-8899-aabbccddeeff";
    const parent = entry(
      "deleted-library-event",
      "2026-10-31T01:05:00.123456Z",
      "library.deleted",
      operationId,
      null,
      "library",
      "library-1",
    );
    parent.libraryId = "library-1";
    const deletedDocument = entry(
      "deleted-document",
      "2026-10-31T01:05:00.123455Z",
      "document.deleted",
      operationId,
      null,
      "document",
    );
    deletedDocument.libraryId = "library-1";
    const mapping = entry(
      "removed-mapping",
      "2026-10-31T01:05:00.123454Z",
      "repository.library_mapping_changed",
      operationId,
      null,
      "repository",
    );
    mapping.libraryId = "library-1";
    const unrelated = entry(
      "other-library-document",
      "2026-10-31T01:05:00.123453Z",
      "document.deleted",
      operationId,
      null,
      "document",
    );
    unrelated.libraryId = "library-2";

    const items = groupAuditFeedEntries(
      [parent, deletedDocument, mapping, unrelated],
      {},
    );

    expect(items.map((item) => item.kind)).toEqual([
      "library-deletion",
      "event",
    ]);
    const deletion = items[0];
    expect(deletion?.kind === "library-deletion" && deletion.parent.id).toBe(
      parent.id,
    );
    expect(
      deletion?.kind === "library-deletion" &&
        deletion.children.map(({ id }) => id),
    ).toEqual([deletedDocument.id, mapping.id]);
    expect(items[0]?.occurredAtUtc).toBe(parent.occurredAtUtc);
  });

  it("does not group operation-linked deletes without a valid library parent", () => {
    const operationId = "00112233-4455-6677-8899-aabbccddeeff";
    const child = entry(
      "deleted-document",
      "2026-10-31T01:05:00Z",
      "document.deleted",
      operationId,
      null,
      "document",
    );
    child.libraryId = "library-1";

    const items = groupAuditFeedEntries([child], {});

    expect(items).toEqual([
      expect.objectContaining({ kind: "event", entry: child }),
    ]);
  });
});
