import { describe, expect, it } from "vitest";
import {
  mapAuditEventDetail,
  mapAuditEventSummary,
  type AuditEventSummaryDto,
} from "./audit-history-mappers";

const event: AuditEventSummaryDto = {
  id: "event-1",
  occurredAtUtc: "2026-10-31T01:05:00Z",
  scope: "Organization",
  organizationLabel: "Example",
  eventType: "document.updated",
  changeType: "Edited",
  area: "Content",
  subjectType: "document",
  subjectId: "doc-1",
  subjectLabel: "guides/review.md",
  libraryId: "library-1",
  actor: {
    kind: "client_credential",
    id: "credential-id",
    displayName: "Build agent",
    channel: "api",
    credentialName: "deploy-prod",
    responsibleUserId: "human-owner-id",
  },
  operationId: "operation-1",
  changedFields: ["path", "credentialSecret", "admin_state"],
  sourceReference: "run-42",
  hasSnapshots: true,
};

describe("audit history API mappers", () => {
  it("uses only event-time actor identity and safe metadata labels", () => {
    expect(mapAuditEventSummary(event)).toMatchObject({
      actorLabel: "deploy-prod",
      actorChannelLabel: "via API credential",
      actorIsSystemAdmin: false,
      subjectLabel: "guides/review.md",
      sourceLabel: "run-42",
      areaLabel: "Content",
    });
  });

  it("verifies sync run references against the event operation id", () => {
    const operationId = "00112233-4455-6677-8899-aabbccddeeff";
    const mappedRun = mapAuditEventSummary({
      ...event,
      eventType: "document.synced",
      operationId,
      sourceReference: "sync-run:run_00112233445566778899aabbccddeeff",
    });
    expect(mappedRun.verifiedSyncRunId).toBe(
      "00112233445566778899aabbccddeeff",
    );

    const fallbackDelete = mapAuditEventSummary({
      ...event,
      eventType: "document.synced",
      operationId,
      sourceReference: "external-id-delete:notion:page-1",
    });
    expect(fallbackDelete.verifiedSyncRunId).toBeNull();

    const mismatchedRun = mapAuditEventSummary({
      ...event,
      eventType: "document.synced",
      operationId,
      sourceReference: "sync-run:run_ffeeddccbbaa99887766554433221100",
    });
    expect(mismatchedRun.verifiedSyncRunId).toBeNull();
  });

  it("shows allowlisted scalar fields and local document snapshots only", () => {
    const detail = mapAuditEventDetail({
      event,
      before: {
        path: "guides/review.md",
        content: "old content",
        fields: {
          path: "guides/review.md",
          credentialSecret: "must not appear",
          admin_state: "inactive",
        },
      },
      after: {
        path: "guides/review.md",
        content: "new content",
        fields: {
          path: "guides/review.md",
          credentialSecret: "must not appear",
          adminState: "active",
        },
      },
    });

    expect(detail.changes).toEqual([
      {
        label: "Path",
        before: "guides/review.md",
        after: "guides/review.md",
        sensitive: false,
      },
      {
        label: "Credential secret",
        before: null,
        after: null,
        sensitive: true,
      },
      {
        label: "Admin state",
        before: "inactive",
        after: "active",
        sensitive: false,
      },
    ]);
    expect(detail.beforeSnapshot?.content).toBe("old content");
    expect(detail.beforeSnapshot?.versionLabel).toBe("Before this change");
    expect(detail.afterSnapshot?.fileName).toBe("review.md");
    expect(detail.afterSnapshot?.versionLabel).toBe("After this change");
    expect(detail.recordedAtUtc).toBe("2026-10-31T01:05:00Z");
    expect(JSON.stringify(detail)).not.toContain("must not appear");

    const syncDetail = mapAuditEventDetail({
      event: {
        ...event,
        eventType: "document.synced",
        sourceReference: "sync-run:run_42",
        changedFields: ["characterCount", "externalId"],
      },
      before: {
        content: "historical synced text",
        fields: { characterCount: "17", externalId: "notion-page-1" },
      },
      after: {
        content: "new synced text",
        fields: { characterCount: "15", externalId: "notion-page-1" },
      },
    });
    expect(syncDetail.beforeSnapshot).toBeNull();
    expect(syncDetail.afterSnapshot).toBeNull();
    expect(syncDetail.sync).toEqual({
      sourceReference: "sync-run:run_42",
      operationId: "operation-1",
    });
    expect(syncDetail.changes).toEqual([
      {
        label: "Character count",
        before: "17",
        after: "15",
        sensitive: false,
      },
      {
        label: "External ID",
        before: "notion-page-1",
        after: "notion-page-1",
        sensitive: false,
      },
    ]);
  });
});
