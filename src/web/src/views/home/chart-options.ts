/**
 * ECharts option builders + data transforms for the home dashboard panels.
 *
 * Tab components stay lean by computing view models here: the metrics store hands
 * back raw DTO arrays and each builder turns one panel's DTOs into a ready-to-bind
 * `EChartsOption`. Every option includes `dataZoom` so vue-echarts' smart diffing
 * preserves the user's zoom/brush across auto-refresh (the option ref is replaced,
 * never `setOption`-d imperatively).
 */
import type {
  BarSeriesOption,
  EChartsOption,
  LineSeriesOption,
  PieSeriesOption,
  ScatterSeriesOption,
} from "echarts";
import type {
  MetricLeaderboardItem,
  MetricPercentilePoint,
  MetricScatterPoint,
  MetricSeriesPoint,
  ReviewFindingsPoint,
  ReviewVolumePoint,
} from "@/api/generated";
import {
  toMetricNumber,
  type MetricWindowRangeMs,
} from "@/stores/metrics-store";

/** A grouped time series pivoted to a shared bucket axis, series ordered by total desc. */
export type PivotedSeries = {
  bucketLabels: string[];
  series: { name: string; values: number[] }[];
};

/** Label shown for a null series key (ungrouped or missing dimension value). */
const ungroupedLabel = "All";

/** Numeric finding-severity fields on a review-findings point. */
type SeverityKey = "critical" | "major" | "minor" | "suggestion" | "comment";

/**
 * Severity bands for the findings bar (UI-3), fixed order (critical first).
 * Colors come from the active chart theme's palette, not a fixed mapping —
 * band order is what keeps "critical" the same stack position across
 * refreshes/themes, not a hardcoded color.
 */
const severityBands: { key: SeverityKey; name: string }[] = [
  { key: "critical", name: "Critical" },
  { key: "major", name: "Major" },
  { key: "minor", name: "Minor" },
  { key: "suggestion", name: "Suggestion" },
  { key: "comment", name: "Comment" },
];

/**
 * Pivots bucketed, single-dimension points onto a shared, time-sorted bucket
 * axis. Missing (bucket, series) cells become 0 so stacking aligns; series are
 * returned largest-total first for stable stack/legend order.
 *
 * When `windowRange` is given, the bucket axis is the *full* set of buckets
 * for the window — including ones with no data — rather than just the
 * buckets that happen to appear in `points`. The API only returns buckets
 * with at least one point, so without this, empty buckets are silently
 * dropped from the x-axis: unrelated non-adjacent buckets end up drawn next
 * to each other with no gap, which looks like grouped/side-by-side bars
 * instead of one (possibly single-segment) stacked bar per bucket. Bucket
 * edges are anchored off an observed point rather than computed independently
 * (the server computes `windowStart = UtcNow - span` fresh per request, so a
 * client-computed origin would drift from the server's by however much time
 * elapsed between the two `now` reads) — since every edge in one response
 * shares the same `date_bin` origin, one observed edge fixes the phase for
 * the whole grid, and points snap to the nearest bucket rather than requiring
 * exact timestamp equality (robust to sub-bucket skew across separate
 * requests, e.g. when multiple metric types are merged into one panel).
 *
 * NOTE: a reviewer flagged anchoring off an observed point (rather than an
 * independently-computed window start) as a possible axis phase-shift risk.
 * This is intentional, not a bug — see above: a client-computed origin would
 * itself drift from the server's per-request `windowStart`, which is what
 * anchoring off a real response edge avoids.
 */
export function pivotByBucket<T>(
  points: T[],
  bucketOf: (point: T) => Date,
  seriesKeyOf: (point: T) => string | null,
  valueOf: (point: T) => number,
  windowRange?: MetricWindowRangeMs,
): PivotedSeries {
  if (points.length === 0) {
    return { bucketLabels: [], series: [] };
  }

  // Kubb types `bucket` as `Date`, but the client does not deserialize JSON
  // date-time strings into real `Date` instances — it arrives as a string at
  // runtime, so always route through `new Date(...)` rather than calling
  // `.getTime()` directly on `bucketOf(point)`.
  const bucketTimeOf = (point: T) => new Date(bucketOf(point)).getTime();

  let bucketTimesMs: number[];
  let indexForBucket: (bucketMs: number) => number | undefined;

  if (windowRange) {
    const { spanMs, bucketMs } = windowRange;
    const anchorMs = bucketTimeOf(points[0]);
    const latestEdgeMs =
      anchorMs + Math.floor((Date.now() - anchorMs) / bucketMs) * bucketMs;
    // floor, not round: every current window preset divides evenly, but floor keeps
    // the grid deterministic (matching latestEdgeMs's flooring) if that ever changes.
    const bucketCount = Math.max(1, Math.floor(spanMs / bucketMs));
    const firstEdgeMs = latestEdgeMs - (bucketCount - 1) * bucketMs;
    bucketTimesMs = Array.from(
      { length: bucketCount },
      (_, index) => firstEdgeMs + index * bucketMs,
    );
    indexForBucket = (pointMs) => {
      const index = Math.round((pointMs - firstEdgeMs) / bucketMs);
      return index >= 0 && index < bucketCount ? index : undefined;
    };
  } else {
    bucketTimesMs = [...new Set(points.map(bucketTimeOf))].sort(
      (left, right) => left - right,
    );
    const bucketIndex = new Map(
      bucketTimesMs.map((time, index) => [time, index]),
    );
    indexForBucket = (pointMs) => bucketIndex.get(pointMs);
  }

  const seriesValues = new Map<string, number[]>();
  for (const point of points) {
    const name = seriesKeyOf(point) ?? ungroupedLabel;
    const index = indexForBucket(bucketTimeOf(point));
    if (index === undefined) {
      continue;
    }
    let values = seriesValues.get(name);
    if (!values) {
      values = new Array(bucketTimesMs.length).fill(0);
      seriesValues.set(name, values);
    }
    values[index] += valueOf(point);
  }

  const series = [...seriesValues.entries()]
    .map(([name, values]) => ({ name, values }))
    .sort((left, right) => sum(right.values) - sum(left.values));

  return {
    bucketLabels: bucketTimesMs.map((time) =>
      formatBucketLabel(new Date(time)),
    ),
    series,
  };
}

/** Maximum distinct series/slices before the tail folds into a single "Other". */
export const maxStackedSeries = 8;

/** Label for the aggregated tail series/slice. */
const otherLabel = "Other";

/**
 * Caps a pivoted series to the top `maxSeries` by total, folding the rest into a
 * single summed "Other" band so high-cardinality groupings (e.g. tool calls by
 * user — UI-1) stay readable. Series arrive total-desc from pivotByBucket.
 */
export function capSeries(
  pivot: PivotedSeries,
  maxSeries = maxStackedSeries,
): PivotedSeries {
  if (pivot.series.length <= maxSeries) {
    return pivot;
  }
  const kept = pivot.series.slice(0, maxSeries - 1);
  const tail = pivot.series.slice(maxSeries - 1);
  const otherValues = pivot.bucketLabels.map((_, index) =>
    tail.reduce((total, entry) => total + entry.values[index], 0),
  );
  return {
    bucketLabels: pivot.bucketLabels,
    series: [...kept, { name: otherLabel, values: otherValues }],
  };
}

/** Builds a stacked bar option from a pivoted series (UI-1/2/6). */
export function timeSeriesOption(
  pivot: PivotedSeries,
  options: { maxSeries?: number; showLegend?: boolean } = {},
): EChartsOption {
  const capped = capSeries(pivot, options.maxSeries ?? maxStackedSeries);
  const showLegend = options.showLegend ?? true;
  // `id` (not just `name`) matters here: vue-echarts inspects each refresh's
  // series array and forces a replaceMerge on `series` whenever the count
  // shrinks between updates (e.g. the top-N+"Other" cap or natural user/tool
  // churn drops the active series count) — see planUpdate/buildSignature in
  // vue-echarts' ECharts.ts. Replace Merge matches existing components by
  // `id` only, never `name`, so without a stable `id` every series gets
  // dropped and recreated on those refreshes instead of animating — looks
  // like a full redraw. `name` is already unique per chart, so reuse it.
  const seriesDefs: BarSeriesOption[] = capped.series.map((entry) => ({
    id: entry.name,
    type: "bar",
    name: entry.name,
    stack: "total",
    emphasis: { focus: "series" },
    data: entry.values,
  }));

  return {
    tooltip: { trigger: "axis" },
    // Vertical scrollable strip on the right rather than the bottom
    // horizontal pager: with 10-20+ series (e.g. many distinct users) the
    // bottom legend paginates ~5 at a time behind arrow clicks, which is
    // unusable; the vertical scroll list stays legible at any series count
    // while keeping the built-in hover-to-highlight/click-to-toggle behavior.
    legend: showLegend
      ? {
          type: "scroll",
          orient: "vertical",
          right: 4,
          top: 8,
          bottom: 8,
          width: 140,
        }
      : { show: false },
    grid: {
      left: 48,
      right: showLegend ? 168 : 24,
      top: 24,
      bottom: 40,
      containLabel: true,
    },
    xAxis: {
      type: "category",
      boundaryGap: true,
      data: capped.bucketLabels,
    },
    yAxis: { type: "value" },
    dataZoom: [
      { id: "inside", type: "inside" },
      { id: "slider", type: "slider", height: 18, bottom: 8 },
    ],
    series: seriesDefs,
  };
}

/**
 * Builds the finding-severity stacked bar with repo/author/origin on the
 * category axis (UI-3). Horizontal orientation — these groupings are always
 * low-cardinality (a handful of repos/authors/origins), so a horizontal
 * layout reads better than a vertical one and gives long category names
 * (repo paths, origins) room to breathe.
 */
export function severityBarOption(
  points: ReviewFindingsPoint[],
): EChartsOption {
  const totalsByKey = new Map<string, Record<string, number>>();
  for (const point of points) {
    const key = point.seriesKey ?? ungroupedLabel;
    const bands = totalsByKey.get(key) ?? {};
    for (const band of severityBands) {
      bands[band.key] =
        (bands[band.key] ?? 0) + toMetricNumber(point[band.key]);
    }
    totalsByKey.set(key, bands);
  }

  // ECharts draws the first category at the bottom, so reverse to keep the
  // same top-to-bottom reading order the vertical layout had left-to-right.
  const categories = [...totalsByKey.keys()].reverse();
  // Band count is fixed (always 5), so this chart isn't exposed to the
  // series-count-shrink replaceMerge issue timeSeriesOption has, but a
  // stable `id` is still correct/cheap — see the note there.
  const series: BarSeriesOption[] = severityBands.map((band) => ({
    id: band.key,
    type: "bar",
    name: band.name,
    stack: "severity",
    emphasis: { focus: "series" },
    data: categories.map((key) => totalsByKey.get(key)?.[band.key] ?? 0),
  }));

  return {
    tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
    legend: { bottom: 0 },
    grid: { left: 8, right: 24, top: 24, bottom: 48, containLabel: true },
    xAxis: { type: "value" },
    yAxis: { type: "category", data: categories },
    series,
  };
}

/** Builds bucketed review-volume stacked bars over time (UI-4). */
export function volumeBarOption(
  points: ReviewVolumePoint[],
  windowRange: MetricWindowRangeMs,
): EChartsOption {
  const pivot = pivotByBucket(
    points,
    (point) => point.bucket,
    (point) => point.seriesKey,
    (point) => toMetricNumber(point.count),
    windowRange,
  );
  return timeSeriesOption(pivot);
}

/** Caps pie slices to the top `maxSlices` by value, folding the rest into "Other". */
function capPieData(
  entries: { name: string; value: number }[],
  maxSlices = maxStackedSeries,
): { name: string; value: number }[] {
  const sorted = [...entries].sort((left, right) => right.value - left.value);
  if (sorted.length <= maxSlices) {
    return sorted;
  }
  const kept = sorted.slice(0, maxSlices - 1);
  const otherValue = sorted
    .slice(maxSlices - 1)
    .reduce((total, entry) => total + entry.value, 0);
  return [...kept, { name: otherLabel, value: otherValue }];
}

/** Builds a donut EChartsOption from pre-summed (name, value) totals, capped to Top-N + Other. */
function donutOptionFromTotals(
  entries: { name: string; value: number }[],
  isDark: boolean,
): EChartsOption {
  const series: PieSeriesOption[] = [
    {
      id: "main",
      type: "pie",
      radius: ["45%", "70%"],
      itemStyle: { borderRadius: 4, borderWidth: 2 },
      // Without an explicit color, ECharts' outer labels default to
      // `outsideFill: 'auto'` — it auto-toggles black-fill/white-stroke vs.
      // white-fill/black-stroke per label to guarantee contrast against an
      // unpredictable background. That stroke is the "bloom"/fringe around
      // the text. We already know the (themed) chart background, so set the
      // color explicitly per mode and skip the auto-contrast guess entirely.
      label: { color: isDark ? "#999" : "#3f3f46" },
      data: capPieData(entries),
    },
  ];

  return {
    tooltip: { trigger: "item", formatter: "{b}: {c} ({d}%)" },
    legend: {
      type: "scroll",
      orient: "vertical",
      right: 4,
      top: 8,
      bottom: 8,
      width: 140,
    },
    series,
  };
}

/** Sums a points array's values into (name, value) totals keyed by series key. */
function totalsBySeriesKey<T>(
  points: T[],
  seriesKeyOf: (point: T) => string | null,
  valueOf: (point: T) => number,
): { name: string; value: number }[] {
  const totals = new Map<string, number>();
  for (const point of points) {
    const key = seriesKeyOf(point) ?? ungroupedLabel;
    totals.set(key, (totals.get(key) ?? 0) + valueOf(point));
  }
  return [...totals.entries()].map(([name, value]) => ({ name, value }));
}

/** Builds a donut of total review volume by series key/origin (UI-5). */
export function volumeDonutOption(
  points: ReviewVolumePoint[],
  isDark: boolean,
): EChartsOption {
  return donutOptionFromTotals(
    totalsBySeriesKey(
      points,
      (point) => point.seriesKey,
      (point) => toMetricNumber(point.count),
    ),
    isDark,
  );
}

/** Builds a donut of total tool calls by dimension (e.g. tool name). */
export function toolCallDonutOption(
  points: MetricSeriesPoint[],
  isDark: boolean,
): EChartsOption {
  return donutOptionFromTotals(
    totalsBySeriesKey(
      points,
      (point) => point.seriesKey,
      (point) => toMetricNumber(point.value),
    ),
    isDark,
  );
}

/** Builds the p50/p95/p99 line option for a histogram metric (UI-8/9). */
export function percentileLinesOption(
  points: MetricPercentilePoint[],
): EChartsOption {
  const buckets = [...points].sort(
    (left, right) =>
      new Date(left.bucket).getTime() - new Date(right.bucket).getTime(),
  );
  const labels = buckets.map((point) => formatBucketLabel(point.bucket));
  const lines: {
    name: string;
    pick: (point: MetricPercentilePoint) => number;
  }[] = [
    { name: "p50", pick: (point) => toMetricNumber(point.p50) },
    { name: "p95", pick: (point) => toMetricNumber(point.p95) },
    { name: "p99", pick: (point) => toMetricNumber(point.p99) },
  ];
  const series: LineSeriesOption[] = lines.map((line) => ({
    id: line.name,
    type: "line",
    name: line.name,
    showSymbol: false,
    emphasis: { focus: "series" },
    data: buckets.map(line.pick),
  }));

  return {
    tooltip: { trigger: "axis" },
    legend: { bottom: 0 },
    grid: { left: 56, right: 24, top: 24, bottom: 56, containLabel: true },
    xAxis: { type: "category", data: labels },
    yAxis: { type: "value" },
    dataZoom: [
      { id: "inside", type: "inside" },
      { id: "slider", type: "slider", height: 18, bottom: 28 },
    ],
    series,
  };
}

/** Builds the duration-vs-tokens scatter; drops samples with no token count (UI-8/9). */
export function tokenScatterOption(
  points: MetricScatterPoint[],
): EChartsOption {
  const data = points
    .filter((point) => point.tokens !== null)
    .map((point) => [
      toMetricNumber(point.tokens),
      toMetricNumber(point.metricValue),
    ]);
  const series: ScatterSeriesOption[] = [
    { id: "main", type: "scatter", symbolSize: 8, data },
  ];

  return {
    tooltip: {
      trigger: "item",
      formatter: (params: unknown) => {
        const value = (params as { value: [number, number] }).value;
        return `${value[0]} tokens · ${value[1]} ms`;
      },
    },
    // Axis names default to nameLocation: "end" (flush with the last tick,
    // clipped by the grid edge) — center them along the axis instead, and
    // rotate the y-axis name vertical (its natural reading direction)
    // rather than the default horizontal text squeezed above the axis.
    grid: { left: 72, right: 24, top: 24, bottom: 56, containLabel: true },
    xAxis: {
      type: "value",
      name: "Tokens",
      nameLocation: "center",
      nameGap: 28,
    },
    yAxis: {
      type: "value",
      name: "Duration (ms)",
      nameLocation: "center",
      nameGap: 44,
      nameRotate: 90,
    },
    series,
  };
}

/** Builds a ranked horizontal bar of the most-read paths (UI-7). */
export function leaderboardBarOption(
  items: MetricLeaderboardItem[],
  isDark: boolean,
): EChartsOption {
  // ECharts draws the first category at the bottom, so reverse to put the
  // highest-ranked path at the top.
  const ordered = [...items].reverse();
  const values = ordered.map((item) => toMetricNumber(item.value));

  // Label sits just past the bar's tip rather than inside it, so it's always
  // drawn on the plain chart background (never the bar color) — the theme
  // JSON leaves textStyle empty, so ECharts' own default label color would
  // otherwise be a fixed black regardless of mode; set explicitly instead.
  // No value label: the count is available on hover via the tooltip.
  const labelWidth = 180;
  const labelColor = isDark ? "#999" : "#3f3f46";

  const series: BarSeriesOption[] = [
    {
      id: "main",
      type: "bar",
      data: values,
      barWidth: "60%",
      itemStyle: { borderRadius: 3 },
      label: {
        show: true,
        position: "right",
        overflow: "truncate",
        width: labelWidth,
        color: labelColor,
        formatter: (params) =>
          ordered[(params as { dataIndex: number }).dataIndex]?.item ?? "",
      },
    },
  ];

  return {
    tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
    // right reserves room for the outside label: without it, bars near the
    // axis max leave the label nowhere to draw and it gets crushed against
    // the chart edge instead of trailing the bar.
    grid: {
      left: 8,
      right: labelWidth + 16,
      top: 8,
      bottom: 8,
      containLabel: true,
    },
    xAxis: { type: "value" },
    yAxis: {
      type: "category",
      data: ordered.map((item) => item.item),
      axisLabel: { show: false },
      axisLine: { show: false },
      axisTick: { show: false },
    },
    series,
  };
}

/** Formats a bucket ISO timestamp as a compact local label. */
export function formatBucketLabel(bucket: Date | string): string {
  return new Date(bucket).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function sum(values: number[]): number {
  return values.reduce((total, value) => total + value, 0);
}
