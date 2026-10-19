import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { ref, watch } from "vue";
import type { MeResponse, MetricSeriesPoint } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { absolutePeriod, relativePeriod } from "@/stores/metric-period";
import { sharedMetricPeriodState } from "@/stores/metric-period-state";
import {
  shouldReloadLibraryMetricsOnUtcRollover,
  useLibraryMetricsStore,
} from "./library-metrics-store";

const apiMocks = vi.hoisted(() => ({
  getMetricSeries: vi.fn(),
  getMetricLeaderboard: vi.fn(),
  getMetricSectionLeaderboard: vi.fn(),
}));

vi.mock("@/api/generated", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/api/generated")>();
  return { ...actual, Metrics: apiMocks };
});

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function point(iso: string, value: number): MetricSeriesPoint {
  return { bucket: new Date(iso), seriesKey: null, value };
}

function testUser(): MeResponse {
  return {
    userId: "usr_caller",
    subject: "usr_caller",
    organizationId: "org_123",
    teamId: null,
    provider: "test",
    providerSubject: "usr_caller",
    name: "Caller",
    email: "caller@example.com",
    pictureUrl: null,
    organizationRole: "member",
    organizationSlug: "test-org",
    organizations: [],
    aliases: [],
    isSystemAdmin: false,
  };
}

describe("useLibraryMetricsStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.resetAllMocks();
    apiMocks.getMetricLeaderboard.mockResolvedValue([]);
    apiMocks.getMetricSectionLeaderboard.mockResolvedValue([]);
    sharedMetricPeriodState.period.value = relativePeriod("24h");
    useAppStore().user = testUser();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("reloads a visible custom range after its final UTC day ends", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T23:59:00.000Z"));
    apiMocks.getMetricSeries.mockResolvedValue([
      point("2026-10-19T00:00:00.000Z", 4),
    ]);
    const store = useLibraryMetricsStore();
    const period = absolutePeriod({
      startDate: "2026-10-19",
      endDate: "2026-10-19",
    });
    store.applyPeriod(period);

    await store.loadMetrics("docs");

    expect(store.chartDomain.mode).toBe("absolute");
    if (store.chartDomain.mode !== "absolute") {
      throw new Error("Expected a custom chart domain.");
    }
    expect(store.chartDomain.effectiveEndMs).toBe(
      Date.parse("2026-10-19T23:59:00.000Z"),
    );

    expect(
      shouldReloadLibraryMetricsOnUtcRollover(
        period,
        "2026-10-19",
        "docs",
        true,
      ),
    ).toBe(true);
    expect(
      shouldReloadLibraryMetricsOnUtcRollover(
        period,
        "2026-10-18",
        "docs",
        true,
      ),
    ).toBe(false);
    expect(
      shouldReloadLibraryMetricsOnUtcRollover(period, "2026-10-19", null, true),
    ).toBe(false);
    expect(
      shouldReloadLibraryMetricsOnUtcRollover(
        period,
        "2026-10-19",
        "docs",
        false,
      ),
    ).toBe(false);
    expect(
      shouldReloadLibraryMetricsOnUtcRollover(
        relativePeriod("24h"),
        "2026-10-19",
        "docs",
        true,
      ),
    ).toBe(false);

    vi.setSystemTime(new Date("2026-10-20T00:01:00.000Z"));
    expect(
      shouldReloadLibraryMetricsOnUtcRollover(
        period,
        "2026-10-19",
        "docs",
        true,
      ),
    ).toBe(true);
    await store.loadMetrics("docs");

    expect(store.chartDomain.mode).toBe("absolute");
    if (store.chartDomain.mode !== "absolute") {
      throw new Error("Expected a custom chart domain.");
    }
    expect(store.chartDomain.effectiveEndMs).toBe(
      store.chartDomain.selectedEndMs,
    );
    expect(apiMocks.getMetricSeries).toHaveBeenCalledTimes(6);
    for (const [, , query] of apiMocks.getMetricSeries.mock.calls) {
      expect(query).toMatchObject({
        startDate: "2026-10-19",
        endDate: "2026-10-19",
        libraries: ["docs"],
      });
      expect(query).not.toHaveProperty("window");
    }
  });

  it("shares the applied period with Home and queries it as window XOR dates", async () => {
    apiMocks.getMetricSeries.mockResolvedValue([]);
    const store = useLibraryMetricsStore();

    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
    );
    await store.loadMetrics("docs");

    const [, , query] = apiMocks.getMetricSeries.mock.calls[0];
    expect(query).toMatchObject({
      startDate: "2026-09-01",
      endDate: "2026-09-03",
      libraries: ["docs"],
    });
    expect(query).not.toHaveProperty("window");
  });

  it("drops a slow response for the previous period and hides its numbers immediately", async () => {
    const slow = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(slow.promise)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([])
      .mockResolvedValue([point("2026-09-05T00:00:00Z", 3)]);
    const store = useLibraryMetricsStore();

    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
    );
    const first = store.loadMetrics("docs");
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-04", endDate: "2026-09-06" }),
    );

    expect(store.loadingReads).toBe(true);
    expect(store.documentReadSeries).toEqual([]);

    slow.resolve([point("2026-09-02T00:00:00Z", 99)]);
    await first;

    expect(store.documentReadSeries).toEqual([]);
  });

  it("invalidates a pending response synchronously when the active organization changes", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T22:59:00.000Z"));
    const slowDocuments = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(slowDocuments.promise)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([])
      .mockResolvedValue([]);
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-10-19", endDate: "2026-10-19" }),
    );
    const oldLoad = store.loadMetrics("docs");

    useAppStore().user = { ...testUser(), organizationId: "org_456" };
    expect(store.loadingReads).toBe(true);
    expect(store.documentReadSeries).toEqual([]);

    // This would be invalid for the old response clock; stale work must not surface it.
    slowDocuments.resolve([point("2026-10-19T23:00:00.000Z", 99)]);
    await oldLoad;

    expect(store.documentReadSeries).toEqual([]);
    expect(store.error).toBeNull();
    expect(store.loadingReads).toBe(true);

    await store.loadMetrics("docs");
    expect(apiMocks.getMetricSeries).toHaveBeenCalledTimes(6);
    expect(
      apiMocks.getMetricSeries.mock.calls.slice(3).map(([orgId]) => orgId),
    ).toEqual(["org_456", "org_456", "org_456"]);
    expect(store.loadingReads).toBe(false);
  });

  it("does not commit a successful response from the previous organization", async () => {
    const slowDocuments = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(slowDocuments.promise)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([])
      .mockResolvedValue([]);
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
    );
    const oldLoad = store.loadMetrics("docs");

    useAppStore().user = { ...testUser(), organizationId: "org_456" };
    slowDocuments.resolve([point("2026-09-02T00:00:00.000Z", 99)]);
    await oldLoad;

    expect(store.documentReadSeries).toEqual([]);
    expect(store.error).toBeNull();
    expect(store.loadingReads).toBe(true);

    await store.loadMetrics("docs");
    expect(apiMocks.getMetricSeries).toHaveBeenCalledTimes(6);
    expect(
      apiMocks.getMetricSeries.mock.calls.slice(3).map(([orgId]) => orgId),
    ).toEqual(["org_456", "org_456", "org_456"]);
    expect(store.loadingReads).toBe(false);
  });

  it("reloads synchronously on a Library change and ignores the prior Library response", async () => {
    const oldDocuments = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(oldDocuments.promise)
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([point("2026-09-02T00:00:00.000Z", 2)])
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([]);
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
    );
    const activeLibraryName = ref<string | null>("library-a");
    const loads: Promise<void>[] = [];
    const stop = watch(
      activeLibraryName,
      (libraryName) => loads.push(store.loadMetrics(libraryName)),
      { immediate: true, flush: "sync" },
    );

    activeLibraryName.value = "library-b";
    expect(loads).toHaveLength(2);
    expect(apiMocks.getMetricSeries).toHaveBeenCalledTimes(6);

    await loads[1];
    expect(store.documentReadSeries).toEqual([
      point("2026-09-02T00:00:00.000Z", 2),
    ]);

    oldDocuments.resolve([point("2026-09-02T00:00:00.000Z", 99)]);
    await loads[0];
    stop();

    expect(store.documentReadSeries).toEqual([
      point("2026-09-02T00:00:00.000Z", 2),
    ]);
    expect(store.error).toBeNull();
    expect(
      apiMocks.getMetricSeries.mock.calls.map(([, , query]) => query.libraries),
    ).toEqual([
      ["library-a"],
      ["library-a"],
      ["library-a"],
      ["library-b"],
      ["library-b"],
      ["library-b"],
    ]);
  });

  it("accepts a Library bucket that elapsed while the grouped response was in flight", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T22:59:00.000Z"));
    const lateDocuments = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(lateDocuments.promise)
      .mockResolvedValueOnce([point("2026-10-19T22:00:00.000Z", 2)])
      .mockResolvedValueOnce([point("2026-10-19T22:00:00.000Z", 3)]);
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-10-19", endDate: "2026-10-19" }),
    );
    const load = store.loadMetrics("docs");

    vi.setSystemTime(new Date("2026-10-19T23:01:00.000Z"));
    lateDocuments.resolve([point("2026-10-19T23:00:00.000Z", 1)]);
    await load;

    expect(store.documentReadSeries).toEqual([
      point("2026-10-19T23:00:00.000Z", 1),
    ]);
    expect(store.sectionReadSeries).toHaveLength(1);
    expect(store.snippetReadSeries).toHaveLength(1);
    expect(store.error).toBeNull();
  });

  it("fails the whole Library reads panel when a response contains a future bucket", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T22:59:00.000Z"));
    const futureDocuments = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(futureDocuments.promise)
      .mockResolvedValueOnce([point("2026-10-19T22:00:00.000Z", 2)])
      .mockResolvedValueOnce([point("2026-10-19T22:00:00.000Z", 3)]);
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-10-19", endDate: "2026-10-19" }),
    );
    const load = store.loadMetrics("docs");
    futureDocuments.resolve([point("2026-10-19T23:00:00.000Z", 1)]);
    await load;

    expect(store.error).toContain("outside the selected or elapsed period");
    expect(store.documentReadSeries).toEqual([]);
    expect(store.sectionReadSeries).toEqual([]);
    expect(store.snippetReadSeries).toEqual([]);
  });

  it("fails the reads panel when buckets fall outside the selected dates", async () => {
    apiMocks.getMetricSeries.mockResolvedValue([
      point("2026-09-20T00:00:00Z", 1),
    ]);
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
    );

    await store.loadMetrics("docs");

    expect(store.error).toMatch(/outside the selected or elapsed period/);
    expect(store.documentReadSeries).toEqual([]);
  });

  it("keeps last successful data during a same-period refresh", async () => {
    apiMocks.getMetricSeries.mockResolvedValue([
      point("2026-09-02T00:00:00Z", 4),
    ]);
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
    );
    await store.loadMetrics("docs");

    apiMocks.getMetricSeries.mockReturnValue(new Promise(() => {}));
    void store.loadMetrics("docs");

    expect(store.documentReadSeries).toHaveLength(1);
  });

  it("clears populated data and panel errors when no Library is selected", async () => {
    apiMocks.getMetricSeries.mockResolvedValue([
      point("2026-09-02T00:00:00Z", 4),
    ]);
    apiMocks.getMetricLeaderboard.mockRejectedValueOnce(
      new Error("previous leaderboard failure"),
    );
    const store = useLibraryMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
    );

    await store.loadMetrics("docs");
    expect(store.documentReadSeries).toHaveLength(1);
    expect(store.error).toBe("previous leaderboard failure");

    await store.loadMetrics(null);

    expect(store.documentReadSeries).toEqual([]);
    expect(store.sectionReadSeries).toEqual([]);
    expect(store.snippetReadSeries).toEqual([]);
    expect(store.leaderboard).toEqual([]);
    expect(store.sectionLeaderboard).toEqual([]);
    expect(store.snippetLeaderboard).toEqual([]);
    expect(store.errors).toEqual({});
    expect(store.error).toBeNull();
    expect(store.loadingReads).toBe(false);
    expect(store.loadingLeaderboard).toBe(false);
    expect(store.loadingSectionLeaderboard).toBe(false);
    expect(store.loadingSnippetLeaderboard).toBe(false);
    expect(store.refreshing).toBe(false);
  });

  it.each([
    ["no Library", false],
    ["no active organization", true],
  ])(
    "clears a pending request for %s and ignores its late success/error",
    async (_name, removeOrganization) => {
      const slowDocuments = deferred<MetricSeriesPoint[]>();
      const slowLeaderboard = deferred<never>();
      apiMocks.getMetricSeries
        .mockReturnValueOnce(slowDocuments.promise)
        .mockResolvedValueOnce([])
        .mockResolvedValueOnce([]);
      apiMocks.getMetricLeaderboard.mockReturnValueOnce(
        slowLeaderboard.promise,
      );
      const store = useLibraryMetricsStore();
      store.applyPeriod(
        absolutePeriod({ startDate: "2026-09-01", endDate: "2026-09-03" }),
      );
      const pending = store.loadMetrics("docs");

      if (removeOrganization) {
        useAppStore().user = null;
        await store.loadMetrics("docs");
      } else {
        await store.loadMetrics(null);
      }

      expect(store.documentReadSeries).toEqual([]);
      expect(store.sectionReadSeries).toEqual([]);
      expect(store.snippetReadSeries).toEqual([]);
      expect(store.leaderboard).toEqual([]);
      expect(store.sectionLeaderboard).toEqual([]);
      expect(store.snippetLeaderboard).toEqual([]);
      expect(store.errors).toEqual({});
      expect(store.error).toBeNull();
      expect(store.loadingReads).toBe(false);
      expect(store.loadingLeaderboard).toBe(false);
      expect(store.loadingSectionLeaderboard).toBe(false);
      expect(store.loadingSnippetLeaderboard).toBe(false);
      expect(store.refreshing).toBe(false);

      slowDocuments.resolve([point("2026-09-02T00:00:00Z", 99)]);
      slowLeaderboard.reject(new Error("late request failure"));
      await pending;

      expect(store.documentReadSeries).toEqual([]);
      expect(store.sectionReadSeries).toEqual([]);
      expect(store.snippetReadSeries).toEqual([]);
      expect(store.leaderboard).toEqual([]);
      expect(store.sectionLeaderboard).toEqual([]);
      expect(store.snippetLeaderboard).toEqual([]);
      expect(store.errors).toEqual({});
      expect(store.error).toBeNull();
      expect(store.loadingReads).toBe(false);
      expect(store.loadingLeaderboard).toBe(false);
      expect(store.loadingSectionLeaderboard).toBe(false);
      expect(store.loadingSnippetLeaderboard).toBe(false);
      expect(store.refreshing).toBe(false);
    },
  );
});
