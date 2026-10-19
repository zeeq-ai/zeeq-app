import { describe, expect, it } from "vitest";
import type { MetricSeriesPoint } from "@/api/generated";
import {
  resolveChartDomain,
  type MetricChartDomain,
} from "@/stores/metric-period";
import {
  formatBucketLabel,
  formatTimestamp,
  percentileLinesOption,
  pivotByBucket,
  reviewCostScatterOption,
  timeSeriesOption,
} from "./chart-options";

const hourMs = 3_600_000;
const dayMs = 24 * hourMs;
const startDate = "2026-09-01";
const startMs = Date.UTC(2026, 8, 1);

/** The backend bucket table, restated independently of the frontend helper under test. */
const serverBucketHours: [dates: number, hours: number][] = [
  [1, 1],
  [2, 2],
  [3, 2],
  [4, 6],
  [7, 6],
  [8, 12],
  [14, 12],
  [15, 24],
  [30, 24],
];

function endDateFor(dates: number): string {
  return new Date(startMs + (dates - 1) * dayMs).toISOString().slice(0, 10);
}

function point(
  bucketMs: number,
  value: number,
  seriesKey: string | null = null,
) {
  return {
    bucket: new Date(bucketMs),
    seriesKey,
    value,
  } satisfies MetricSeriesPoint;
}

function absoluteDomain(dates: number, nowMs: number): MetricChartDomain {
  return resolveChartDomain(
    { mode: "absolute", startDate, endDate: endDateFor(dates) },
    nowMs,
  );
}

const farFuture = Date.UTC(2027, 0, 1);

describe("pivotByBucket on a custom (absolute) domain", () => {
  const emptyPoints: MetricSeriesPoint[] = [];

  it("keeps the elapsed UTC grid when a custom range has no telemetry", () => {
    const days = 15;
    const bucketStarts = Array.from(
      { length: days },
      (_, index) => startMs + index * dayMs,
    );
    const pivot = pivotByBucket(
      emptyPoints,
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(days, farFuture),
    );

    expect(pivot).toEqual({
      bucketTimesMs: bucketStarts,
      series: [],
      utc: true,
      outOfDomainCount: 0,
    });
  });

  it("keeps a today-only custom grid empty at UTC midnight", () => {
    const pivot = pivotByBucket(
      emptyPoints,
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(1, startMs),
    );

    expect(pivot).toEqual({
      bucketTimesMs: [],
      series: [],
      utc: true,
      outOfDomainCount: 0,
    });
  });

  it.each(serverBucketHours)(
    "%i selected date(s): every server bucket start maps to its own bin and the total is conserved",
    (dates, hours) => {
      const bucketMs = hours * hourMs;
      const serverStarts = Array.from(
        { length: (dates * dayMs) / bucketMs },
        (_, index) => startMs + index * bucketMs,
      );
      const points = serverStarts.map((ms, index) => point(ms, index + 1));

      const pivot = pivotByBucket(
        points,
        (p) => p.bucket,
        (p) => p.seriesKey,
        (p) => Number(p.value),
        absoluteDomain(dates, farFuture),
      );

      expect(pivot.bucketTimesMs).toEqual(serverStarts);
      expect(pivot.outOfDomainCount).toBe(0);
      const values = pivot.series[0].values;
      expect(values).toEqual(serverStarts.map((_, index) => index + 1));
      expect(new Set(values).size).toBe(serverStarts.length);
      expect(values.reduce((a, b) => a + b, 0)).toBe(
        points.reduce((a, p) => a + Number(p.value), 0),
      );
    },
  );

  it("maps by floor index, so a point inside a bucket is kept in that bucket", () => {
    const pivot = pivotByBucket(
      [point(startMs + 30 * 60_000, 5), point(startMs + 3 * hourMs + 59, 7)],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(2, farFuture),
    );

    expect(pivot.series[0].values.slice(0, 2)).toEqual([5, 7]);
  });

  it("counts, never silently folds, a point outside the selected dates", () => {
    const pivot = pivotByBucket(
      [
        point(startMs, 1),
        point(startMs + dayMs, 9),
        point(startMs - hourMs, 9),
      ],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(1, farFuture),
    );

    expect(pivot.outOfDomainCount).toBe(2);
    expect(pivot.series[0].values.reduce((a, b) => a + b, 0)).toBe(1);
  });

  it("zero-fills only elapsed buckets for a range that includes today", () => {
    const now = startMs + 3 * hourMs + 10 * 60_000;
    const pivot = pivotByBucket(
      [point(startMs + hourMs, 4)],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(1, now),
    );

    // 03:10 UTC elapsed => buckets 00,01,02,03 (the in-progress 03:00 bucket included), never 04+.
    expect(pivot.bucketTimesMs).toEqual(
      [0, 1, 2, 3].map((h) => startMs + h * hourMs),
    );
  });

  it("keeps a late bucket after the resolved clock advances past it", () => {
    const now = startMs + 4 * hourMs + 60_000;
    const pivot = pivotByBucket(
      [point(startMs + 4 * hourMs, 2)],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(1, now),
    );

    expect(pivot.bucketTimesMs.at(-1)).toBe(startMs + 4 * hourMs);
    expect(pivot.series[0].values.at(-1)).toBe(2);
    expect(pivot.outOfDomainCount).toBe(0);
  });

  it("does not extend the elapsed grid with a genuinely future bucket", () => {
    const now = startMs + 3 * hourMs + 59 * 60_000;
    const pivot = pivotByBucket(
      [point(startMs + 4 * hourMs, 2)],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(1, now),
    );

    expect(pivot.bucketTimesMs).toEqual(
      [0, 1, 2, 3].map((hour) => startMs + hour * hourMs),
    );
    expect(pivot.series).toEqual([]);
    expect(pivot.outOfDomainCount).toBe(1);
  });

  it("starts at the selected start, not at now, for a historical range", () => {
    const pivot = pivotByBucket(
      [point(startMs + 6 * hourMs, 1)],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(4, Date.UTC(2026, 9, 19)),
    );

    expect(pivot.bucketTimesMs[0]).toBe(startMs);
    expect(pivot.bucketTimesMs).toHaveLength(16);
    expect(pivot.utc).toBe(true);
  });

  it("produces no buckets at exactly UTC midnight of a today-only range", () => {
    const pivot = pivotByBucket<MetricSeriesPoint>(
      [],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      absoluteDomain(1, startMs),
    );

    expect(pivot.bucketTimesMs).toEqual([]);
  });
});

describe("pivotByBucket on rolling presets", () => {
  it("keeps the epoch-aligned grid and non-UTC mode", () => {
    const domain = resolveChartDomain({ mode: "relative", window: "24h" }, 0);
    const bucketStart = Math.floor(Date.now() / hourMs) * hourMs;

    const pivot = pivotByBucket(
      [point(bucketStart, 3)],
      (p) => p.bucket,
      (p) => p.seriesKey,
      (p) => Number(p.value),
      domain,
    );

    expect(pivot.utc).toBe(false);
    expect(pivot.bucketTimesMs).toHaveLength(24);
    expect(pivot.series[0].values.at(-1)).toBe(3);
  });
});

describe("explicit UTC chart options", () => {
  const sample = [point(startMs, 1)];

  function optionFor(domain: MetricChartDomain) {
    return timeSeriesOption(
      pivotByBucket(
        sample,
        (p) => p.bucket,
        (p) => p.seriesKey,
        (p) => Number(p.value),
        domain,
      ),
    );
  }

  it("sets useUTC true for custom ranges and explicitly false for presets (no stale merge)", () => {
    expect(optionFor(absoluteDomain(1, farFuture)).useUTC).toBe(true);
    expect(
      optionFor(resolveChartDomain({ mode: "relative", window: "24h" }, 0))
        .useUTC,
    ).toBe(false);
  });

  it("formats labels and tooltips in UTC independent of the runtime zone", () => {
    const instant = new Date(Date.UTC(2026, 8, 1, 23, 30));

    expect(formatBucketLabel(instant, true)).toContain("11:30");
    expect(formatTimestamp(instant, true)).toContain("UTC");
    // Local formatting is untouched for presets.
    expect(formatTimestamp(instant, false)).toBe(instant.toLocaleString());
  });

  it("sets useUTC on the review-cost scatter and percentile lines in both modes", () => {
    expect(reviewCostScatterOption([], true).useUTC).toBe(true);
    expect(reviewCostScatterOption([], false).useUTC).toBe(false);
    expect(percentileLinesOption([], true).useUTC).toBe(true);
    expect(percentileLinesOption([], false).useUTC).toBe(false);
  });
});
