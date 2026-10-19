import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type {
  FindingReviewListItemResponse,
  MeResponse,
  MetricSeriesPoint,
  MetricsOverview,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import {
  absolutePeriod,
  relativePeriod,
  type MetricDateRange,
} from "@/stores/metric-period";
import { sharedMetricPeriodState } from "@/stores/metric-period-state";
import { createCoalescingRunner } from "@/views/home/useMetricsAutoRefresh";
import { useMetricsStore } from "./metrics-store";

const apiMocks = vi.hoisted(() => ({
  getMetricsOverview: vi.fn(),
  getMetricSeries: vi.fn(),
  getMetricSectionLeaderboard: vi.fn(),
  listFindingReviews: vi.fn(),
  getMetricsFilterOptions: vi.fn(),
}));

vi.mock("@/api/generated", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/api/generated")>();
  return { ...actual, Metrics: apiMocks };
});

/** A response the test resolves or rejects on its own schedule. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

afterEach(() => {
  vi.useRealTimers();
});

const septemberOneToThree: MetricDateRange = {
  startDate: "2026-09-01",
  endDate: "2026-09-03",
};
const septemberFourToSix: MetricDateRange = {
  startDate: "2026-09-04",
  endDate: "2026-09-06",
};

function overviewWith(toolCalls: number): MetricsOverview {
  return {
    toolCalls,
    knowledgeReads: 0,
    reviews: 0,
    criticalFindings: 0,
    majorFindings: 0,
    p95ReviewDurationMs: 0,
  };
}

function seriesPoint(bucket: Date, value: number): MetricSeriesPoint {
  return { bucket, seriesKey: "u", value };
}

function finding(reviewId: string): FindingReviewListItemResponse {
  return {
    reviewId,
    title: reviewId,
    authorLogin: "a",
    ownerQualifiedRepoName: "o/r",
    requestOrigin: "RepositoryWebhook",
    pullRequestNumber: 1,
    url: "https://example.test",
    createdAtUtc: new Date("2026-09-02T00:00:00Z"),
    groupCriticalFindings: 1,
    groupMajorFindings: 0,
  } as unknown as FindingReviewListItemResponse;
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

describe("useMetricsStore period and request races", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.resetAllMocks();
    sharedMetricPeriodState.period.value = relativePeriod("24h");
    useAppStore().user = testUser();
  });

  it("sends only window for presets and only plain date strings for custom ranges", async () => {
    apiMocks.getMetricsOverview.mockResolvedValue(overviewWith(1));
    const store = useMetricsStore();

    await store.loadOverview();
    store.applyPeriod(absolutePeriod(septemberOneToThree));
    await store.loadOverview();

    const [, presetQuery] = apiMocks.getMetricsOverview.mock.calls[0];
    const [, customQuery] = apiMocks.getMetricsOverview.mock.calls[1];
    expect(presetQuery).toEqual({ window: "24h" });
    expect(presetQuery).not.toHaveProperty("startDate");
    expect(customQuery).toEqual({
      startDate: "2026-09-01",
      endDate: "2026-09-03",
    });
    expect(customQuery).not.toHaveProperty("window");
  });

  it("paints only the latest period when an older slow response lands last", async () => {
    const slowA = deferred<MetricsOverview>();
    const fastB = deferred<MetricsOverview>();
    apiMocks.getMetricsOverview
      .mockReturnValueOnce(slowA.promise)
      .mockReturnValueOnce(fastB.promise);
    const store = useMetricsStore();

    store.applyPeriod(absolutePeriod(septemberOneToThree));
    const loadA = store.loadOverview();
    store.applyPeriod(absolutePeriod(septemberFourToSix));
    const loadB = store.loadOverview();

    fastB.resolve(overviewWith(222));
    await loadB;
    slowA.resolve(overviewWith(111));
    await loadA;

    expect(store.overview?.toolCalls).toBe(222);
    expect(store.isLoading("overview")).toBe(false);
    expect(store.error).toBeNull();
  });

  it("ignores a stale rejection and never labels it under the new period", async () => {
    const slowA = deferred<MetricsOverview>();
    apiMocks.getMetricsOverview
      .mockReturnValueOnce(slowA.promise)
      .mockResolvedValueOnce(overviewWith(5));
    const store = useMetricsStore();

    store.applyPeriod(absolutePeriod(septemberOneToThree));
    const loadA = store.loadOverview();
    store.applyPeriod(absolutePeriod(septemberFourToSix));
    await store.loadOverview();
    slowA.reject(new Error("boom"));
    await expect(loadA).resolves.toBeUndefined();

    expect(store.error).toBeNull();
    expect(store.overview?.toolCalls).toBe(5);
  });

  it("clears old numbers immediately and reports loading (not empty) until the new load begins", async () => {
    apiMocks.getMetricsOverview.mockResolvedValueOnce(overviewWith(9));
    const store = useMetricsStore();
    await store.loadOverview();
    expect(store.overview?.toolCalls).toBe(9);

    store.applyPeriod(absolutePeriod(septemberOneToThree));

    expect(store.overview).toBeNull();
    expect(store.isLoading("overview")).toBe(true);
    expect(store.isLoading("anything")).toBe(true);

    const pending = deferred<MetricsOverview>();
    apiMocks.getMetricsOverview.mockReturnValueOnce(pending.promise);
    const load = store.loadOverview();
    expect(store.isLoading("overview")).toBe(true);
    pending.resolve(overviewWith(1));
    await load;

    expect(store.isLoading("overview")).toBe(false);
  });

  it("invalidates in-flight responses when a filter changes, before the reload starts", async () => {
    const stale = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries.mockReturnValueOnce(stale.promise);
    const store = useMetricsStore();

    const load = store.loadToolCallSeries();
    store.filterUsers = ["someone@example.com"];
    stale.resolve([seriesPoint(new Date(), 4)]);
    await load;

    expect(store.toolCallSeries).toEqual([]);
    expect(store.isLoading("toolCallSeries")).toBe(true);
  });

  it("keeps a failed panel's error when a sibling later succeeds, and labels the root alert from current failures", async () => {
    apiMocks.getMetricsOverview.mockRejectedValueOnce(
      new Error("overview down"),
    );
    apiMocks.getMetricSeries.mockResolvedValue([]);
    const store = useMetricsStore();

    await expect(store.loadOverview()).rejects.toThrow("overview down");
    await store.loadToolCallSeries();

    expect(store.errors).toHaveProperty("overview", "overview down");
    expect(store.error).toBe("overview down");
    expect(store.isLoading("toolCallSeries")).toBe(false);

    apiMocks.getMetricsOverview.mockResolvedValueOnce(overviewWith(2));
    await store.loadOverview();

    expect(store.error).toBeNull();
  });

  it("rejects a response whose buckets fall outside the selected dates instead of showing partial totals", async () => {
    apiMocks.getMetricSeries.mockResolvedValue([
      seriesPoint(new Date("2026-09-02T00:00:00Z"), 1),
      seriesPoint(new Date("2026-09-09T00:00:00Z"), 1),
    ]);
    const store = useMetricsStore();
    store.applyPeriod(absolutePeriod(septemberOneToThree));

    await expect(store.loadToolCallSeries()).rejects.toThrow(
      /outside the selected or elapsed period/,
    );

    expect(store.toolCallSeries).toEqual([]);
    expect(store.error).toMatch(/outside the selected or elapsed period/);
  });

  it("commits a grouped panel atomically: one failing series commits none", async () => {
    apiMocks.getMetricSeries
      .mockResolvedValueOnce([seriesPoint(new Date(), 1)])
      .mockRejectedValueOnce(new Error("sections down"))
      .mockResolvedValueOnce([seriesPoint(new Date(), 1)]);
    const store = useMetricsStore();

    await expect(store.loadKnowledgeSeries()).rejects.toThrow("sections down");

    expect(store.documentReadSeries).toEqual([]);
    expect(store.snippetReadSeries).toEqual([]);
  });

  it("accepts a custom bucket that elapses while Home's grouped request is in flight", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T22:59:00.000Z"));
    const lateDocuments = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(lateDocuments.promise)
      .mockResolvedValueOnce([
        seriesPoint(new Date("2026-10-19T22:00:00.000Z"), 2),
      ])
      .mockResolvedValueOnce([
        seriesPoint(new Date("2026-10-19T22:00:00.000Z"), 3),
      ]);
    const store = useMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-10-19", endDate: "2026-10-19" }),
    );
    const load = store.loadKnowledgeSeries();

    vi.setSystemTime(new Date("2026-10-19T23:01:00.000Z"));
    lateDocuments.resolve([
      seriesPoint(new Date("2026-10-19T23:00:00.000Z"), 1),
    ]);
    await load;

    expect(store.documentReadSeries).toEqual([
      seriesPoint(new Date("2026-10-19T23:00:00.000Z"), 1),
    ]);
    expect(store.sectionReadSeries).toHaveLength(1);
    expect(store.snippetReadSeries).toHaveLength(1);
    expect(store.error).toBeNull();
  });

  it("fails the whole Home grouped panel when a response contains a future bucket", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T22:59:00.000Z"));
    const futureDocuments = deferred<MetricSeriesPoint[]>();
    apiMocks.getMetricSeries
      .mockReturnValueOnce(futureDocuments.promise)
      .mockResolvedValueOnce([
        seriesPoint(new Date("2026-10-19T22:00:00.000Z"), 2),
      ])
      .mockResolvedValueOnce([
        seriesPoint(new Date("2026-10-19T22:00:00.000Z"), 3),
      ]);
    const store = useMetricsStore();
    store.applyPeriod(
      absolutePeriod({ startDate: "2026-10-19", endDate: "2026-10-19" }),
    );
    const load = store.loadKnowledgeSeries();
    futureDocuments.resolve([
      seriesPoint(new Date("2026-10-19T23:00:00.000Z"), 1),
    ]);
    await expect(load).rejects.toThrow(
      "outside the selected or elapsed period",
    );

    expect(store.error).toContain("outside the selected or elapsed period");
    expect(store.documentReadSeries).toEqual([]);
    expect(store.sectionReadSeries).toEqual([]);
    expect(store.snippetReadSeries).toEqual([]);
  });

  it("does not reject a batch until every sibling request has settled", async () => {
    const slow = deferred<never[]>();
    apiMocks.getMetricSectionLeaderboard
      .mockRejectedValueOnce(new Error("fast failure"))
      .mockReturnValueOnce(slow.promise);
    const store = useMetricsStore();

    let settled = false;
    const batch = store.loadSectionLeaderboard().then(
      () => (settled = true),
      () => (settled = true),
    );
    await flush();

    expect(settled).toBe(false);

    slow.resolve([]);
    await batch;
    expect(settled).toBe(true);
  });

  it("keeps the period placeholder until a coalesced current batch starts after old siblings settle", async () => {
    const oldOverview = deferred<MetricsOverview>();
    const currentOverview = deferred<MetricsOverview>();
    apiMocks.getMetricsOverview
      .mockReturnValueOnce(oldOverview.promise)
      .mockReturnValueOnce(currentOverview.promise);
    apiMocks.getMetricSeries.mockResolvedValue([]);
    const store = useMetricsStore();
    store.applyPeriod(absolutePeriod(septemberOneToThree));

    const runner = createCoalescingRunner(async () => {
      await Promise.allSettled([
        store.loadOverview(),
        store.loadToolCallSeries(),
      ]);
    });
    const oldBatch = runner.request();
    expect(apiMocks.getMetricsOverview).toHaveBeenCalledTimes(1);
    expect(apiMocks.getMetricSeries).toHaveBeenCalledTimes(1);

    store.applyPeriod(absolutePeriod(septemberFourToSix));
    expect(store.overview).toBeNull();
    expect(store.isLoading("overview")).toBe(true);
    const currentBatch = runner.request();

    // The old batch cannot begin a later sibling after invalidation: all sibling
    // actions were started synchronously, and the trailing batch waits for settlement.
    expect(apiMocks.getMetricsOverview).toHaveBeenCalledTimes(1);
    expect(apiMocks.getMetricSeries).toHaveBeenCalledTimes(1);
    expect(store.isLoading("overview")).toBe(true);

    oldOverview.resolve(overviewWith(111));
    await oldBatch;
    await flush();

    expect(apiMocks.getMetricsOverview).toHaveBeenCalledTimes(2);
    expect(apiMocks.getMetricSeries).toHaveBeenCalledTimes(2);
    expect(store.overview).toBeNull();
    expect(store.isLoading("overview")).toBe(true);
    const [, currentQuery] = apiMocks.getMetricsOverview.mock.calls[1];
    expect(currentQuery).toEqual({
      startDate: "2026-09-04",
      endDate: "2026-09-06",
    });

    currentOverview.resolve(overviewWith(222));
    await currentBatch;

    expect(store.overview).toEqual(overviewWith(222));
    expect(store.isLoading("overview")).toBe(false);
  });
});

describe("findings drill-down pagination", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.resetAllMocks();
    sharedMetricPeriodState.period.value = relativePeriod("24h");
    useAppStore().user = testUser();
  });

  const periodA = absolutePeriod(septemberOneToThree);
  const periodB = absolutePeriod(septemberFourToSix);

  it("resets a pending load-more when a new first page starts, and B ends not-loading with no stale error", async () => {
    const store = useMetricsStore();
    apiMocks.listFindingReviews.mockResolvedValueOnce({
      items: [finding("a1")],
      nextCursor: "cursor-a",
    });
    await store.loadFindingReviews("Critical", periodA);

    const moreA = deferred<{
      items: FindingReviewListItemResponse[];
      nextCursor: string | null;
    }>();
    apiMocks.listFindingReviews.mockReturnValueOnce(moreA.promise);
    const loadMoreA = store.loadMoreFindingReviews("Critical", periodA);
    expect(store.isLoading("findingReviews:Critical:more")).toBe(true);

    apiMocks.listFindingReviews.mockResolvedValueOnce({
      items: [finding("b1")],
      nextCursor: "cursor-b",
    });
    await store.loadFindingReviews("Critical", periodB);

    moreA.reject(new Error("old load-more failed"));
    await loadMoreA;

    expect(store.findingReviewItems.Critical.map((i) => i.reviewId)).toEqual([
      "b1",
    ]);
    expect(store.findingReviewNextCursor.Critical).toBe("cursor-b");
    expect(store.loading["findingReviews:Critical:more"]).toBe(false);
    expect(store.errors).not.toHaveProperty("findingReviews:Critical:more");
    expect(store.error).toBeNull();
  });

  it("discards a stale load-more result instead of appending it to the new chain", async () => {
    const store = useMetricsStore();
    apiMocks.listFindingReviews.mockResolvedValueOnce({
      items: [finding("a1")],
      nextCursor: "cursor-a",
    });
    await store.loadFindingReviews("Critical", periodA);
    const moreA = deferred<{
      items: FindingReviewListItemResponse[];
      nextCursor: string | null;
    }>();
    apiMocks.listFindingReviews.mockReturnValueOnce(moreA.promise);
    const loadMoreA = store.loadMoreFindingReviews("Critical", periodA);
    apiMocks.listFindingReviews.mockResolvedValueOnce({
      items: [finding("b1")],
      nextCursor: null,
    });
    await store.loadFindingReviews("Critical", periodB);

    moreA.resolve({ items: [finding("a2")], nextCursor: "cursor-a2" });
    await loadMoreA;

    expect(store.findingReviewItems.Critical.map((i) => i.reviewId)).toEqual([
      "b1",
    ]);
    expect(store.findingReviewNextCursor.Critical).toBeNull();
  });

  it("will not send a cursor under a different period than the one that produced it", async () => {
    const store = useMetricsStore();
    apiMocks.listFindingReviews.mockResolvedValueOnce({
      items: [finding("a1")],
      nextCursor: "cursor-a",
    });
    await store.loadFindingReviews("Critical", periodA);
    apiMocks.listFindingReviews.mockClear();

    await store.loadMoreFindingReviews("Critical", periodB);

    expect(apiMocks.listFindingReviews).not.toHaveBeenCalled();
  });

  it("sends the same period and the unchanged cursor on page two, leaving the other severity alone", async () => {
    const store = useMetricsStore();
    apiMocks.listFindingReviews
      .mockResolvedValueOnce({ items: [finding("m1")], nextCursor: "cm" })
      .mockResolvedValueOnce({ items: [finding("c1")], nextCursor: "cursor-a" })
      .mockResolvedValueOnce({ items: [finding("c2")], nextCursor: null });
    await store.loadFindingReviews("Major", periodA);
    await store.loadFindingReviews("Critical", periodA);

    await store.loadMoreFindingReviews("Critical", periodA);

    const [, pageTwoQuery] = apiMocks.listFindingReviews.mock.calls[2];
    expect(pageTwoQuery).toEqual({
      startDate: "2026-09-01",
      endDate: "2026-09-03",
      severity: "Critical",
      cursor: "cursor-a",
    });
    expect(store.findingReviewItems.Critical.map((i) => i.reviewId)).toEqual([
      "c1",
      "c2",
    ]);
    expect(store.findingReviewItems.Major.map((i) => i.reviewId)).toEqual([
      "m1",
    ]);
  });

  it("keeps the local findings page when only the dashboard period changes", async () => {
    const store = useMetricsStore();
    apiMocks.listFindingReviews.mockResolvedValueOnce({
      items: [finding("a1")],
      nextCursor: null,
    });
    await store.loadFindingReviews("Critical", periodA);

    store.applyPeriod(periodB);

    expect(store.findingReviewItems.Critical).toHaveLength(1);
  });
});
