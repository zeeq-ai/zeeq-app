import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useAuditHistoryStore } from "./audit-history-store";

const apiMocks = vi.hoisted(() => ({
  listAuditHistory: vi.fn(),
  listSystemAuditHistory: vi.fn(),
  getAuditHistoryEvent: vi.fn(),
  getSystemAuditHistoryEvent: vi.fn(),
}));

vi.mock("@/api/generated", () => ({
  AuditHistory: apiMocks,
}));

describe("useAuditHistoryStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
  });

  it("passes event timestamps to detail lookup without losing microseconds", async () => {
    const event = auditEvent(
      "event-1",
      "2026-10-31T12:00:00.123456+00:00",
      "document.updated",
    );
    apiMocks.listAuditHistory.mockResolvedValue(page([event]));
    apiMocks.getAuditHistoryEvent.mockResolvedValue({
      event,
      before: null,
      after: null,
    });

    const store = useAuditHistoryStore();
    store.setContext({ scope: "document", subjectType: "document" });
    await store.loadInitial();
    await store.toggleDetail("event-1");

    expect(apiMocks.getAuditHistoryEvent).toHaveBeenCalledWith("event-1", {
      occurredAtUtc: "2026-10-31T12:00:00.123456+00:00",
    });
  });

  it("uses current library labels and clears entries when organization changes", async () => {
    apiMocks.listAuditHistory.mockResolvedValue(
      page([
        auditEvent(
          "library-event",
          "2026-10-31T12:00:00Z",
          "document.updated",
          "operation-1",
          "library-1",
        ),
      ]),
    );

    const store = useAuditHistoryStore();
    const context = {
      scope: "organization" as const,
      organizationId: "organization-1",
      libraryLabels: { "library-1": "Engineering" },
    };
    store.setContext(context);
    await store.loadInitial();

    expect(store.entries[0]?.libraryLabel).toBe("Engineering");

    store.setContext({
      ...context,
      libraryLabels: { "library-1": "Platform" },
    });
    expect(store.entries[0]?.libraryLabel).toBe("Platform");

    store.setContext({
      scope: "organization",
      organizationId: "organization-2",
    });
    expect(store.entries).toEqual([]);
  });

  it("clears secondary filters in one request and preserves the date range", async () => {
    apiMocks.listAuditHistory.mockResolvedValue(page([]));
    const store = useAuditHistoryStore();
    store.setContext({ scope: "organization", organizationId: "org-1" });
    await store.setRange("all-time");
    await store.setTypeFilter("deleted");
    await store.setAreaFilter("Content");
    await store.setActorFilter("actor-1", "Alice");
    apiMocks.listAuditHistory.mockClear();

    await store.clearFilters();

    expect(store.range).toBe("all-time");
    expect(store.typeFilter).toBe("all");
    expect(store.areaFilter).toBe("all");
    expect(store.actorFilter).toBeNull();
    expect(store.actorFilterLabel).toBeNull();
    expect(apiMocks.listAuditHistory).toHaveBeenCalledTimes(1);
    expect(apiMocks.listAuditHistory.mock.calls[0]?.[0]).toMatchObject({
      AllTime: true,
      ChangeType: undefined,
      Area: undefined,
      ActorId: undefined,
    });
    await store.clearFilters();
    expect(apiMocks.listAuditHistory).toHaveBeenCalledTimes(1);
  });

  it("merges entries by exact timestamp then descending UUID", async () => {
    const firstId = "00000000-0000-0000-0000-000000000001";
    const tiedHigherId = "00000000-0000-0000-0000-000000000002";
    const laterFractionId = "00000000-0000-0000-0000-000000000003";
    apiMocks.listAuditHistory
      .mockResolvedValueOnce(
        page([
          auditEvent(
            firstId,
            "2026-10-31T12:00:00.123456+00:00",
            "document.synced",
            "run-1",
          ),
        ]),
      )
      .mockResolvedValueOnce(
        page([
          auditEvent(
            tiedHigherId,
            "2026-10-31T12:00:00.123456+00:00",
            "document.synced",
            "run-1",
          ),
          auditEvent(
            laterFractionId,
            "2026-10-31T12:00:00.123457+00:00",
            "document.synced",
            "run-1",
          ),
        ]),
      );

    const store = useAuditHistoryStore();
    store.setContext({ scope: "organization", libraryId: "library-1" });
    await store.loadInitial();
    await store.loadOperation("run-1");

    expect(store.entries.map(({ id }) => id)).toEqual([
      laterFractionId,
      tiedHigherId,
      firstId,
    ]);
  });

  it("ignores stale operation success and failure after the scope changes", async () => {
    const oldOperationPage = deferred<ReturnType<typeof page>>();
    const currentOperationPage = deferred<ReturnType<typeof page>>();
    apiMocks.listAuditHistory
      .mockResolvedValueOnce(
        page([
          auditEvent(
            "old-base",
            "2026-10-31T12:00:00Z",
            "document.synced",
            "run-1",
          ),
        ]),
      )
      .mockReturnValueOnce(oldOperationPage.promise)
      .mockResolvedValueOnce(
        page([
          auditEvent(
            "new-base",
            "2026-10-31T11:00:00Z",
            "document.synced",
            "run-1",
          ),
        ]),
      )
      .mockReturnValueOnce(currentOperationPage.promise);

    const store = useAuditHistoryStore();
    store.setContext({ scope: "organization", libraryId: "library-old" });
    await store.loadInitial();
    const staleRequest = store.loadOperation("run-1");

    store.setContext({ scope: "organization", libraryId: "library-new" });
    await store.loadInitial();
    const currentRequest = store.loadOperation("run-1");

    oldOperationPage.reject(new Error("stale failure"));
    await staleRequest;
    expect(store.operationStates["run-1"]).toMatchObject({
      loading: true,
      error: null,
    });

    currentOperationPage.resolve(
      page([
        auditEvent(
          "new-operation-entry",
          "2026-10-31T10:00:00Z",
          "document.synced",
          "run-1",
        ),
      ]),
    );
    await currentRequest;

    expect(store.entries.map(({ id }) => id)).toEqual([
      "new-base",
      "new-operation-entry",
    ]);
  });
});

function auditEvent(
  id: string,
  occurredAtUtc: string,
  eventType: string,
  operationId = "operation-1",
  libraryId?: string,
) {
  return {
    id,
    occurredAtUtc,
    scope: "Organization",
    eventType,
    changeType: "Edited",
    area: "Content",
    subjectType: "document",
    subjectId: `subject-${id}`,
    subjectLabel: `${id}.md`,
    libraryId,
    actor: {
      kind: "user",
      id: "actor-1",
      displayName: "Alice",
      channel: "web",
    },
    operationId,
    changedFields: [],
    hasSnapshots: false,
  };
}

function page(entries: ReturnType<typeof auditEvent>[]) {
  return { entries, nextCursor: null, coverage: "partial first slice" };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((innerResolve, innerReject) => {
    resolve = innerResolve;
    reject = innerReject;
  });

  return { promise, resolve, reject };
}
