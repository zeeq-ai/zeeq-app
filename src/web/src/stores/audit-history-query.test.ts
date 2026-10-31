import { describe, expect, it } from "vitest";
import {
  buildAuditHistoryQuery,
  shouldCloseAuditHistoryForContextChange,
} from "./audit-history-query";

describe("audit history query", () => {
  it("keeps filters and scope when requesting the next cursor page", () => {
    expect(
      buildAuditHistoryQuery(
        {
          scope: "document",
          subjectType: "Document",
          subjectId: "doc-7",
          libraryId: "library-4",
        },
        "90-days",
        { type: "edited", area: "all", actorId: "actor-2" },
        { cursor: "next-page" },
      ),
    ).toEqual({
      Cursor: "next-page",
      Limit: 50,
      AllTime: false,
      ChangeType: "Edited",
      Area: undefined,
      ActorId: "actor-2",
      SubjectType: "Document",
      SubjectId: "doc-7",
      LibraryId: "library-4",
      OperationId: undefined,
    });
  });

  it("uses explicit all-time mode and the operation filter for sync continuation", () => {
    expect(
      buildAuditHistoryQuery(
        { scope: "organization", libraryId: "library-4" },
        "all-time",
        { type: "all", area: "Security", actorId: null },
        { operationId: "operation-1" },
      ),
    ).toMatchObject({
      AllTime: true,
      Area: "Security",
      OperationId: "operation-1",
      Limit: 50,
    });
  });

  it("closes an open history feed when its organization, library, or document changes", () => {
    const libraryA = {
      scope: "organization" as const,
      organizationId: "organization-1",
      libraryId: "library-a",
    };
    const libraryB = { ...libraryA, libraryId: "library-b" };
    const documentA = {
      scope: "document" as const,
      organizationId: "organization-1",
      libraryId: "library-a",
      subjectType: "document",
      subjectId: "document-a",
    };

    expect(shouldCloseAuditHistoryForContextChange(true, libraryA, libraryB)).toBe(
      true,
    );
    expect(
      shouldCloseAuditHistoryForContextChange(true, documentA, {
        ...documentA,
        subjectId: "document-b",
      }),
    ).toBe(true);
    expect(
      shouldCloseAuditHistoryForContextChange(true, libraryA, {
        ...libraryA,
        organizationId: "organization-2",
      }),
    ).toBe(true);
    expect(shouldCloseAuditHistoryForContextChange(false, libraryA, libraryB)).toBe(
      false,
    );
  });

  it("keeps the same history scope open when only its display labels change", () => {
    const current = {
      scope: "organization" as const,
      organizationId: "organization-1",
      libraryId: "library-a",
      libraryLabels: { "library-a": "Engineering" },
    };
    const next = {
      ...current,
      libraryLabels: { "library-a": "Platform" },
    };

    expect(shouldCloseAuditHistoryForContextChange(true, current, next)).toBe(
      false,
    );
  });
});
