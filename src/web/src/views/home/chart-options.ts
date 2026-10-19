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
  ReviewCostSample,
  ReviewVolumePoint,
} from "@/api/generated";
import { toMetricNumber } from "@/stores/metrics-store";
import type { MetricChartDomain } from "@/stores/metric-period";

/**
 * A grouped time series pivoted to a shared bucket axis, series ordered by total desc.
 *
 * `bucketTimesMs` (not formatted label strings) is what `timeSeriesOption` uses as each
 * bar's x-axis identity — see the comment there for why that distinction matters for
 * auto-refresh animation.
 */
export type PivotedSeries = {
  bucketTimesMs: number[];
  series: { name: string; values: number[] }[];
  /** True for explicit-UTC (custom date range) charts; drives ECharts `useUTC` and label formatting. */
  utc: boolean;
  /** Custom-domain points outside the elapsed or selected range that were not plotted. */
  outOfDomainCount: number;
};

/** Label shown for a null series key (ungrouped or missing dimension value). */
const ungroupedLabel = "All";

type SeriesLabelOptions = {
  seriesLabel?: (seriesKey: string) => string;
};

type ValueAxisOptions = {
  yAxisName?: string;
  yAxisLabelFormatter?: (value: number) => string;
};

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
 * When `domain` is given, the bucket axis is the *full* set of buckets
 * for the period — including ones with no data — rather than just the
 * buckets that happen to appear in `points`. The API only returns buckets
 * with at least one point, so without this, empty buckets are silently
 * dropped from the x-axis: unrelated non-adjacent buckets end up drawn next
 * to each other with no gap, which looks like grouped/side-by-side bars
 * instead of one (possibly single-segment) stacked bar per bucket.
 *
 * Relative (rolling preset) domains are anchored epoch-aligned
 * (`floor(Date.now() / bucketMs) * bucketMs`), not off a server response point:
 * - Server's `date_bin` origin (`windowStart = UtcNow - span`) is recomputed
 *   per uncached request (`PostgresMetricsQueryStore.GetSeriesAsync`), so
 *   it's not a stable anchor across requests.
 * - Exact server-phase match isn't needed anyway — `indexForBucket` snaps
 *   each point to the *nearest* client bucket, and every bucket width (tens
 *   of minutes+) dwarfs realistic clock/`windowStart` skew.
 * - Epoch alignment keeps `bucketTimesMs[index]` identical across separate
 *   requests for the same logical bucket, which `timeSeriesOption` relies on
 *   for stable per-bar `id`s (see below) — an anchor derived from response
 *   data would drift every time `windowStart` does, i.e. on every cache miss.
 *
 * Absolute (custom UTC range) domains are exact: the server bins from the selected
 * start's UTC midnight, so each point maps to `floor((bucket - start) / bucketMs)`.
 * Only elapsed buckets (before `effectiveEndMs`) are generated so a range that includes
 * today never draws future hours as measured zero. The stores resolve this domain using the
 * response-time clock, separate from the backend query's fixed half-open selected end, so
 * late data is retained once its bucket has elapsed. A genuinely future bucket is counted in
 * `outOfDomainCount` and cannot extend the grid (and store validation rejects it). Points outside the
 * selected dates are also counted there, never silently folded into another bucket.
 */
export function pivotByBucket<T>(
  points: T[],
  bucketOf: (point: T) => Date,
  seriesKeyOf: (point: T) => string | null,
  valueOf: (point: T) => number,
  domain?: MetricChartDomain,
): PivotedSeries {
  const utc = domain?.mode === "absolute";
  // An empty historical custom range still has a selected UTC domain and should
  // keep its elapsed bucket grid. Empty relative periods retain the no-data shape.
  if (points.length === 0 && domain?.mode !== "absolute") {
    return { bucketTimesMs: [], series: [], utc, outOfDomainCount: 0 };
  }

  // Kubb types `bucket` as `Date`, but the client does not deserialize JSON
  // date-time strings into real `Date` instances — it arrives as a string at
  // runtime, so always route through `new Date(...)` rather than calling
  // `.getTime()` directly on `bucketOf(point)`.
  const bucketTimeOf = (point: T) => new Date(bucketOf(point)).getTime();

  let bucketTimesMs: number[];
  let indexForBucket: (bucketMs: number) => number | undefined;

  if (domain?.mode === "absolute") {
    const { startMs, selectedEndMs, effectiveEndMs, bucketMs } = domain;
    const selectedCount = Math.round((selectedEndMs - startMs) / bucketMs);
    const elapsedCount = Math.min(
      selectedCount,
      Math.max(0, Math.ceil((effectiveEndMs - startMs) / bucketMs)),
    );
    indexForBucket = (pointMs) => {
      const index = Math.floor((pointMs - startMs) / bucketMs);
      return index >= 0 && index < elapsedCount ? index : undefined;
    };
    bucketTimesMs = Array.from(
      { length: elapsedCount },
      (_, index) => startMs + index * bucketMs,
    );
  } else if (domain) {
    const { spanMs, bucketMs } = domain;
    // Epoch-aligned, not server-anchored — see the doc comment above for why.
    const latestEdgeMs = Math.floor(Date.now() / bucketMs) * bucketMs;
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
  let outOfDomainCount = 0;
  for (const point of points) {
    const name = seriesKeyOf(point) ?? ungroupedLabel;
    const index = indexForBucket(bucketTimeOf(point));
    if (index === undefined) {
      outOfDomainCount++;
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

  return { bucketTimesMs, series, utc, outOfDomainCount };
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
  const otherValues = pivot.bucketTimesMs.map((_, index) =>
    tail.reduce((total, entry) => total + entry.values[index], 0),
  );
  return {
    ...pivot,
    series: [...kept, { name: otherLabel, values: otherValues }],
  };
}

/** Builds a stacked bar option from a pivoted series (UI-1/2/6). */
export function timeSeriesOption(
  pivot: PivotedSeries,
  options: {
    maxSeries?: number;
    showLegend?: boolean;
  } & SeriesLabelOptions &
    ValueAxisOptions = {},
): EChartsOption {
  const capped = capSeries(pivot, options.maxSeries ?? maxStackedSeries);
  const showLegend = options.showLegend ?? true;
  const seriesLabel = options.seriesLabel ?? identitySeriesLabel;
  // Stable identity, two levels:
  // - Series: `id: entry.name`. vue-echarts replaceMerges `series` by `id`
  //   (never `name`) whenever the count shrinks (top-N cap, user/tool churn)
  //   — see planUpdate/buildSignature in vue-echarts' ECharts.ts.
  // - Per-bar: `id`+`name` set to the bucket's epoch ms, not array index
  //   (breaks when the sliding window shifts) or the formatted label (can
  //   repaint identically at DST edges).
  // - Both `id` *and* `name` are required on each data point — ECharts' data
  //   differ needs `id` to treat an unchanged bucket as an update rather than
  //   a remove+add; `name` alone (the only thing the ECharts dynamic-data
  //   guide documents: https://echarts.apache.org/handbook/en/how-to/data/dynamic-data)
  //   is not sufficient.
  const seriesDefs: BarSeriesOption[] = capped.series.map((entry) => ({
    id: entry.name,
    type: "bar",
    name: entry.name,
    stack: "total",
    emphasis: { focus: "series" },
    data: entry.values.map((value, index) => ({
      id: String(capped.bucketTimesMs[index]),
      name: String(capped.bucketTimesMs[index]),
      value: [capped.bucketTimesMs[index], value],
    })),
  }));

  return {
    // Explicit in both modes: ECharts chooses time-axis ticks and default axis-tooltip
    // headers from `useUTC`, so UTC labels alone are not enough for a custom range, and
    // switching back to a rolling preset must not retain a stale `true` through merging.
    useUTC: capped.utc,
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
          // Align labels to the legend column's inner edge so variable-length
          // names share one clean vertical rail with their color swatches.
          align: "right",
          formatter: seriesLabel,
        }
      : { show: false },
    grid: {
      left: options.yAxisName ? 64 : 48,
      right: showLegend ? 168 : 24,
      top: 24,
      bottom: 40,
      containLabel: true,
    },
    xAxis: {
      type: "time",
      axisLabel: {
        formatter: (value: number) =>
          formatBucketLabel(new Date(value), capped.utc),
        // A category axis only ever showed as many ticks as fit (one per
        // bucket, evenly spaced); a time axis picks its own tick interval
        // independent of our formatted label's width, so wide labels like
        // "Jul 23, 04:00 AM" overlap into unreadable text without this.
        hideOverlap: true,
      },
    },
    yAxis: {
      type: "value",
      name: options.yAxisName,
      nameLocation: options.yAxisName ? "center" : undefined,
      nameGap: options.yAxisName ? 42 : undefined,
      nameRotate: options.yAxisName ? 90 : undefined,
      axisLabel: options.yAxisLabelFormatter
        ? { formatter: options.yAxisLabelFormatter }
        : undefined,
    },
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
  options: SeriesLabelOptions = {},
): EChartsOption {
  const seriesLabel = options.seriesLabel ?? identitySeriesLabel;
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
    yAxis: {
      type: "category",
      data: categories,
      axisLabel: { formatter: seriesLabel },
    },
    series,
  };
}

/** Builds bucketed review-volume stacked bars over time (UI-4). */
export function volumeBarOption(
  points: ReviewVolumePoint[],
  domain: MetricChartDomain,
  options: SeriesLabelOptions = {},
): EChartsOption {
  const pivot = pivotByBucket(
    points,
    (point) => point.bucket,
    (point) => point.seriesKey,
    (point) => toMetricNumber(point.count),
    domain,
  );
  return timeSeriesOption(pivot, options);
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
  options: SeriesLabelOptions = {},
  maxSlices = maxStackedSeries,
): EChartsOption {
  const seriesLabel = options.seriesLabel ?? identitySeriesLabel;
  const series: PieSeriesOption[] = [
    {
      id: "main",
      type: "pie",
      radius: ["45%", "70%"],
      itemStyle: { borderRadius: 0, borderWidth: 0 },
      // Without an explicit color, ECharts' outer labels default to
      // `outsideFill: 'auto'` — it auto-toggles black-fill/white-stroke vs.
      // white-fill/black-stroke per label to guarantee contrast against an
      // unpredictable background. That stroke is the "bloom"/fringe around
      // the text. We already know the (themed) chart background, so set the
      // color explicitly per mode and skip the auto-contrast guess entirely.
      label: {
        color: isDark ? "#999" : "#3f3f46",
        formatter: (params) =>
          seriesLabel(String((params as { name: string }).name)),
      },
      data: capPieData(entries, maxSlices),
    },
  ];

  return {
    tooltip: {
      trigger: "item",
      formatter: (params: unknown) => {
        const item = params as { name: string; value: number; percent: number };
        return `${seriesLabel(item.name)}: ${item.value} (${item.percent}%)`;
      },
    },
    legend: {
      type: "scroll",
      orient: "vertical",
      right: 4,
      top: 8,
      bottom: 8,
      width: 140,
      align: "right",
      formatter: seriesLabel,
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
  options: SeriesLabelOptions = {},
): EChartsOption {
  return donutOptionFromTotals(
    totalsBySeriesKey(
      points,
      (point) => point.seriesKey,
      (point) => toMetricNumber(point.count),
    ),
    isDark,
    options,
  );
}

/** Builds a donut of summed metric values by series key. */
export function metricDonutOption(
  points: MetricSeriesPoint[],
  isDark: boolean,
  maxSlices = maxStackedSeries,
): EChartsOption {
  return donutOptionFromTotals(
    totalsBySeriesKey(
      points,
      (point) => point.seriesKey,
      (point) => toMetricNumber(point.value),
    ),
    isDark,
    {},
    maxSlices,
  );
}

/** Builds a donut of total tool calls by dimension (e.g. tool name). */
export function toolCallDonutOption(
  points: MetricSeriesPoint[],
  isDark: boolean,
  maxSlices = maxStackedSeries,
): EChartsOption {
  return metricDonutOption(points, isDark, maxSlices);
}

function identitySeriesLabel(seriesKey: string): string {
  return seriesKey;
}

/** Max chars kept before middle-truncating a legend label (fits the 140px vertical legend). */
const maxAgentLabelLength = 26;
/** Chars kept from each end when a label exceeds `maxAgentLabelLength`. */
const agentLabelEdgeLength = 12;

/**
 * Truncates long connecting-agent identifiers (e.g.
 * `claude-code/2.1.218 (sdk-ts, agent-sdk/0.3.216)`) for the legend, keeping
 * both the version prefix and the distinguishing suffix (cli/sdk tag) rather
 * than clipping from one end.
 */
export function truncateAgentLabel(seriesKey: string): string {
  if (seriesKey.length <= maxAgentLabelLength) {
    return seriesKey;
  }
  return `${seriesKey.slice(0, agentLabelEdgeLength)}…${seriesKey.slice(-agentLabelEdgeLength)}`;
}

/** Builds the p50/p95/p99 line option for a histogram metric (UI-8/9). */
export function percentileLinesOption(
  points: MetricPercentilePoint[],
  utc = false,
): EChartsOption {
  const buckets = [...points].sort(
    (left, right) =>
      new Date(left.bucket).getTime() - new Date(right.bucket).getTime(),
  );
  const labels = buckets.map((point) => formatBucketLabel(point.bucket, utc));
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
    useUTC: utc,
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

/** Plots one point per completed review; callers can use dataIndex for its deep link. */
export function reviewCostScatterOption(
  points: ReviewCostSample[],
  utc = false,
): EChartsOption {
  const data = points.map((point) => [
    new Date(point.createdAtUtc).getTime(),
    toMetricNumber(point.costUsd),
  ]);
  const series: ScatterSeriesOption[] = [
    { id: "review-cost", type: "scatter", symbolSize: 9, data },
  ];

  return {
    useUTC: utc,
    tooltip: {
      trigger: "item",
      formatter: (params: unknown) => {
        if (
          typeof params !== "object" ||
          params === null ||
          !("dataIndex" in params) ||
          typeof params.dataIndex !== "number"
        ) {
          return "";
        }

        const point = points[params.dataIndex];
        if (!point) {
          return "";
        }

        const author = point.authorLogin
          .replaceAll("&", "&amp;")
          .replaceAll("<", "&lt;");
        return `${author}<br/>${formatTimestamp(point.createdAtUtc, utc)}<br/>$${toMetricNumber(point.costUsd).toFixed(4)} · click to open`;
      },
    },
    grid: { left: 68, right: 24, top: 24, bottom: 55, containLabel: true },
    xAxis: { type: "time" },
    yAxis: { type: "value", name: "USD" },
    dataZoom: [
      { id: "inside", type: "inside" },
      { id: "slider", type: "slider", height: 18, bottom: 8 },
    ],
    series,
  };
}

/** Builds a ranked horizontal bar of the most-read paths (UI-7). */
export function leaderboardBarOption(
  items: MetricLeaderboardItem[],
): EChartsOption {
  // ECharts draws the first category at the bottom, so reverse to put the
  // highest-ranked path at the top.
  const ordered = [...items].reverse();
  const values = ordered.map((item) => toMetricNumber(item.value));

  // Label is drawn inside the bar (not trailing it) so it doesn't force a
  // wide reserved gutter on the right of short/truncated names. Inside a
  // canvas-rendered chart the label can sit over the bar's cyan fill *or*
  // spill onto the plain page background (for a bar too short to hold its
  // own label), and that background flips between white and near-black by
  // theme — no single fill color reads on all three. A text halo sidesteps
  // that: a white fill with a dark outline stays legible regardless of what
  // ends up underneath, so this needs no isDark branching at all.
  // No value label: the count is available on hover via the tooltip.
  // insideLeft always starts the label at the axis origin (x=0), independent
  // of how long the bar itself is, so the usable width is roughly the whole
  // grid — not just the bar's length. Sized close to a typical panel's grid
  // width so truncation only kicks in for genuinely long paths.
  const labelWidth = 420;

  const series: BarSeriesOption[] = [
    {
      id: "main",
      type: "bar",
      data: values,
      barWidth: "75%",
      // Explicit desaturated teal rather than the theme's palette[0] (#06b6d4):
      // the label text is drawn in white directly on top of the bar fill, and
      // the fully-saturated cyan doesn't give it enough contrast to read well.
      itemStyle: { borderRadius: 4, color: "oklch(60.9% 0.126 221.723)" },
      label: {
        show: true,
        position: "insideLeft",
        overflow: "truncate",
        width: labelWidth,
        color: "#fff",
        textBorderColor: "rgba(30, 30, 30, 0.35)",
        textBorderWidth: 2,
        formatter: (params) =>
          ordered[(params as { dataIndex: number }).dataIndex]?.item ?? "",
      },
    },
  ];

  return {
    tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
    grid: {
      left: 8,
      right: 8,
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

/**
 * Formats a bucket timestamp as a compact label: browser-local for rolling presets
 * (unchanged), explicit UTC for custom date ranges.
 */
export function formatBucketLabel(bucket: Date | string, utc = false): string {
  return new Date(bucket).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    ...(utc ? { timeZone: "UTC" } : {}),
  });
}

/** Full date-time for tooltips; custom date ranges are labeled in UTC explicitly. */
export function formatTimestamp(value: Date | string, utc = false): string {
  return new Date(value).toLocaleString(
    undefined,
    utc ? { timeZone: "UTC", timeZoneName: "short" } : undefined,
  );
}

function sum(values: number[]): number {
  return values.reduce((total, value) => total + value, 0);
}
