/**
 * Reporting-period model shared by the Home dashboard, Library metrics and the
 * findings drill-down.
 *
 * A period is either a rolling preset (`window` token, server-resolved against
 * "now") or an explicit, inclusive range of 1–30 UTC calendar dates. Everything
 * here is pure — parsing, validation, labels, query shaping, recent-range
 * history and the chart-domain/bucket policy — so it is unit-testable without
 * Vue, Pinia or a clock. Persistence lives in `metric-period-state.ts`.
 *
 * The bucket policy mirrors the backend (`Zeeq.Core.Models/Metrics`); keep
 * {@link customRangeBucketMs} and {@link metricWindowRangeMs} in sync with it.
 */

/**
 * Fixed window tokens accepted by every metrics route.
 * Ordered shortest→longest so the selector reads naturally.
 */
export const metricWindowTokens = [
  "15m",
  "30m",
  "1h",
  "4h",
  "12h",
  "24h",
  "72h",
  "7d",
  "14d",
  "30d",
] as const;
export type MetricWindowToken = (typeof metricWindowTokens)[number];

export const defaultMetricWindow: MetricWindowToken = "24h";

/** Look-back span and fixed bucket width, in milliseconds, for a window token. */
export type MetricWindowRangeMs = { spanMs: number; bucketMs: number };

const minuteMs = 60_000;
const hourMs = 60 * minuteMs;
export const dayMs = 24 * hourMs;

/**
 * Client mirror of the backend's `MetricWindowExtensions.ToRange()` — keep in
 * sync. Used to fill in empty buckets on the client (the API only returns
 * buckets that have at least one point), so the x-axis shows the true bucket
 * cadence instead of collapsing empty stretches away.
 */
const metricWindowRangesMs: Record<MetricWindowToken, MetricWindowRangeMs> = {
  "15m": { spanMs: 15 * minuteMs, bucketMs: 1 * minuteMs },
  "30m": { spanMs: 30 * minuteMs, bucketMs: 2 * minuteMs },
  "1h": { spanMs: 1 * hourMs, bucketMs: 5 * minuteMs },
  "4h": { spanMs: 4 * hourMs, bucketMs: 15 * minuteMs },
  "12h": { spanMs: 12 * hourMs, bucketMs: 30 * minuteMs },
  "24h": { spanMs: 24 * hourMs, bucketMs: 1 * hourMs },
  "72h": { spanMs: 72 * hourMs, bucketMs: 2 * hourMs },
  "7d": { spanMs: 7 * dayMs, bucketMs: 6 * hourMs },
  "14d": { spanMs: 14 * dayMs, bucketMs: 12 * hourMs },
  "30d": { spanMs: 30 * dayMs, bucketMs: 1 * dayMs },
};

/** Resolves the look-back span and bucket width (ms) for a window token. */
export function metricWindowRangeMs(
  token: MetricWindowToken,
): MetricWindowRangeMs {
  return metricWindowRangesMs[token];
}

/** Human labels for the window selector, keyed by token. */
const windowLabels: Record<MetricWindowToken, string> = {
  "15m": "Last 15 minutes",
  "30m": "Last 30 minutes",
  "1h": "Last hour",
  "4h": "Last 4 hours",
  "12h": "Last 12 hours",
  "24h": "Last 24 hours",
  "72h": "Last 72 hours",
  "7d": "Last 7 days",
  "14d": "Last 14 days",
  "30d": "Last 30 days",
};

/** Ready-made `{ label, value }` items for a rolling-preset select. */
export const metricWindowItems = metricWindowTokens.map((token) => ({
  label: windowLabels[token],
  value: token,
}));

/** Narrows an arbitrary string to a known window token. */
export function isMetricWindowToken(
  value: unknown,
): value is MetricWindowToken {
  return metricWindowTokens.some((token) => token === value);
}

/** Inclusive UTC date interval, as `yyyy-MM-dd` strings (never Date objects or labels). */
export type MetricDateRange = { startDate: string; endDate: string };

/** The applied reporting period: a rolling preset or an explicit UTC date range. */
export type MetricPeriod =
  | { mode: "relative"; window: MetricWindowToken }
  | ({ mode: "absolute" } & MetricDateRange);

/** Longest selectable interval, counting both end dates. */
export const maxMetricRangeDays = 30;

/** Maximum number of remembered custom ranges. */
export const maxRecentMetricRanges = 3;

/**
 * Query parameters for the twelve interval-scoped metrics operations: exactly
 * one mode, so the object can be spread straight into a generated call.
 */
export type MetricPeriodQuery =
  | { window: MetricWindowToken; startDate?: undefined; endDate?: undefined }
  | { window?: undefined; startDate: string; endDate: string };

/** Shapes a period into the mutually exclusive `window` XOR `startDate`/`endDate` query. */
export function metricPeriodQuery(period: MetricPeriod): MetricPeriodQuery {
  return period.mode === "relative"
    ? { window: period.window }
    : { startDate: period.startDate, endDate: period.endDate };
}

/** Stable identity of a period (cache keys, pagination identity, change detection). */
export function metricPeriodKey(period: MetricPeriod): string {
  return period.mode === "relative"
    ? `w:${period.window}`
    : `r:${period.startDate}:${period.endDate}`;
}

export function isSamePeriod(left: MetricPeriod, right: MetricPeriod): boolean {
  return metricPeriodKey(left) === metricPeriodKey(right);
}

export function isSameDateRange(
  left: MetricDateRange,
  right: MetricDateRange,
): boolean {
  return left.startDate === right.startDate && left.endDate === right.endDate;
}

export function relativePeriod(window: MetricWindowToken): MetricPeriod {
  return { mode: "relative", window };
}

export function absolutePeriod(range: MetricDateRange): MetricPeriod {
  return {
    mode: "absolute",
    startDate: range.startDate,
    endDate: range.endDate,
  };
}

// ---------------------------------------------------------------------------
// UTC calendar dates
// ---------------------------------------------------------------------------

const isoDatePattern = /^(\d{4})-(\d{2})-(\d{2})$/;

/** Parses a strict `yyyy-MM-dd` string to UTC midnight (ms); null when malformed or not a real date. */
export function isoDateToUtcMs(value: string): number | null {
  const match = isoDatePattern.exec(value);
  if (!match) {
    return null;
  }
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  if (year < 1 || year > 9999) {
    return null;
  }
  // setUTCFullYear preserves literal years 0001–0099; Date.UTC remaps them into 1901–1999.
  const utcDate = new Date(0);
  const ms = utcDate.setUTCFullYear(year, month - 1, day);
  const roundTrip = new Date(ms);
  // Reject overflow dates such as February 30 and non-leap February 29.
  if (
    roundTrip.getUTCFullYear() !== year ||
    roundTrip.getUTCMonth() !== month - 1 ||
    roundTrip.getUTCDate() !== day
  ) {
    return null;
  }
  return ms;
}

/** Formats UTC midnight (or any instant) as the `yyyy-MM-dd` UTC date containing it. */
export function utcMsToIsoDate(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

/** The UTC calendar date containing `nowMs`. */
export function utcTodayIso(nowMs: number): string {
  return utcMsToIsoDate(nowMs);
}

/** Milliseconds until the next UTC midnight after `nowMs` (always > 0). */
export function msUntilNextUtcMidnight(nowMs: number): number {
  return dayMs - (nowMs % dayMs);
}

/** Inclusive date count between two valid ISO dates; null if either is invalid. */
export function inclusiveDayCount(
  startDate: string,
  endDate: string,
): number | null {
  const start = isoDateToUtcMs(startDate);
  const end = isoDateToUtcMs(endDate);
  if (start === null || end === null) {
    return null;
  }
  return Math.round((end - start) / dayMs) + 1;
}

export type MetricDateRangeIssue =
  | "missing-start"
  | "missing-end"
  | "invalid"
  | "reversed"
  | "too-long"
  | "future";

export type MetricDateRangeValidation =
  | { valid: true; days: number }
  | { valid: false; issue: MetricDateRangeIssue; message: string };

const issueMessages: Record<MetricDateRangeIssue, string> = {
  "missing-start": "Choose a start date.",
  "missing-end": "Choose an end date.",
  invalid: "Enter valid dates.",
  reversed: "The start date must be on or before the end date.",
  "too-long": `Choose at most ${maxMetricRangeDays} dates, including both the start and end date.`,
  future: "Dates after today (UTC) cannot be selected.",
};

/**
 * Validates a (possibly incomplete) draft or stored range against the UTC "today".
 * Never clamps: callers surface `message` instead of silently adjusting dates.
 */
export function validateMetricDateRange(
  startDate: string | null | undefined,
  endDate: string | null | undefined,
  todayUtc: string,
): MetricDateRangeValidation {
  const fail = (issue: MetricDateRangeIssue): MetricDateRangeValidation => ({
    valid: false,
    issue,
    message: issueMessages[issue],
  });
  if (!startDate) {
    return fail("missing-start");
  }
  if (!endDate) {
    return fail("missing-end");
  }
  const days = inclusiveDayCount(startDate, endDate);
  if (days === null) {
    return fail("invalid");
  }
  if (days < 1) {
    return fail("reversed");
  }
  if (days > maxMetricRangeDays) {
    return fail("too-long");
  }
  if (endDate > todayUtc) {
    return fail("future");
  }
  return { valid: true, days };
}

/**
 * Whether a calendar day cannot be picked: any future UTC date, and — while only a start
 * (the pending anchor) is chosen — any date more than 29 days from it, so no pick can exceed
 * 30 inclusive dates. This replaces Reka's `maximumDays`, whose hover preview is wrong; the
 * completed range is still validated independently by {@link validateMetricDateRange}.
 *
 * @param dateIso - Candidate day (yyyy-MM-dd).
 * @param todayUtc - UTC "today" (yyyy-MM-dd).
 * @param anchorIso - Pending start while the end is unselected, else null.
 */
export function isCalendarDateDisabled(
  dateIso: string,
  todayUtc: string,
  anchorIso: string | null,
): boolean {
  if (dateIso > todayUtc) {
    return true;
  }
  if (!anchorIso) {
    return false;
  }
  const distance = inclusiveDayCount(
    dateIso < anchorIso ? dateIso : anchorIso,
    dateIso < anchorIso ? anchorIso : dateIso,
  );
  return distance === null || distance > maxMetricRangeDays;
}

/** Parses a stored value as a range, or null when it is not a currently valid one. */
function parseStoredRange(
  value: unknown,
  todayUtc: string,
): MetricDateRange | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }
  const startDate: unknown = Reflect.get(value, "startDate");
  const endDate: unknown = Reflect.get(value, "endDate");
  if (typeof startDate !== "string" || typeof endDate !== "string") {
    return null;
  }
  return validateMetricDateRange(startDate, endDate, todayUtc).valid
    ? { startDate, endDate }
    : null;
}

/**
 * Keeps only well-formed, in-bounds, deduplicated ranges (newest first, max 3)
 * from untrusted storage, so malformed or hand-edited entries never reach a query.
 */
export function sanitizeRecentRanges(
  raw: unknown,
  todayUtc: string,
): MetricDateRange[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const ranges: MetricDateRange[] = [];
  for (const entry of raw) {
    const range = parseStoredRange(entry, todayUtc);
    if (range && !ranges.some((existing) => isSameDateRange(existing, range))) {
      ranges.push(range);
    }
  }
  return ranges.slice(0, maxRecentMetricRanges);
}

/**
 * Records an applied range: newest first, deduplicated by exact dates (reuse
 * promotes), truncated to three. Applying A/B/C/D yields D/C/B; choosing B → B/D/C.
 */
export function promoteRecentRange(
  history: MetricDateRange[],
  range: MetricDateRange,
): MetricDateRange[] {
  const entry = { startDate: range.startDate, endDate: range.endDate };
  return [
    entry,
    ...history.filter((existing) => !isSameDateRange(existing, entry)),
  ].slice(0, maxRecentMetricRanges);
}

/** Restores a stored rolling-preset token, falling back to the default when unknown. */
export function parseStoredWindow(raw: string | null): MetricWindowToken {
  return isMetricWindowToken(raw) ? raw : defaultMetricWindow;
}

// ---------------------------------------------------------------------------
// Labels
// ---------------------------------------------------------------------------

/**
 * Full, unambiguous label for a UTC range, always with year and "UTC", e.g.
 * "Sep 1 – 3, 2026 · UTC". Cross-month/year ranges show both ends (Intl merges
 * shared parts), and a single date is shown once.
 */
export function formatMetricDateRange(
  range: MetricDateRange,
  locale?: string,
): string {
  const start = isoDateToUtcMs(range.startDate);
  const end = isoDateToUtcMs(range.endDate);
  if (start === null || end === null) {
    return `${range.startDate} – ${range.endDate} · UTC`;
  }
  const formatter = new Intl.DateTimeFormat(locale, {
    month: "short",
    day: "numeric",
    year: "numeric",
    timeZone: "UTC",
  });
  return `${formatter.formatRange(start, end)} · UTC`;
}

/** Label for the applied period: preset name, or the full UTC date range. */
export function metricPeriodLabel(
  period: MetricPeriod,
  locale?: string,
): string {
  return period.mode === "relative"
    ? windowLabels[period.window]
    : formatMetricDateRange(period, locale);
}

/** Whether a custom period ended before today (UTC): fixed data, no live refresh. */
export function isHistoricalPeriod(
  period: MetricPeriod,
  todayUtc: string,
): boolean {
  return period.mode === "absolute" && period.endDate < todayUtc;
}

/** Whether the period's last selected date is today (UTC), so its data is partial/live. */
export function periodIncludesToday(
  period: MetricPeriod,
  todayUtc: string,
): boolean {
  return period.mode === "absolute" && period.endDate === todayUtc;
}

// ---------------------------------------------------------------------------
// Chart domain and custom bucket policy
// ---------------------------------------------------------------------------

/**
 * Bucket width for a custom range of `days` selected dates — the single
 * client-side copy of the backend table:
 * 1 date = 1 h, 2–3 = 2 h, 4–7 = 6 h, 8–14 = 12 h, 15–30 = 1 day.
 * Bucket origin is selected-start UTC midnight; today's elapsed portion never
 * changes the policy.
 */
export function customRangeBucketMs(days: number): number {
  if (days <= 1) {
    return hourMs;
  }
  if (days <= 3) {
    return 2 * hourMs;
  }
  if (days <= 7) {
    return 6 * hourMs;
  }
  if (days <= 14) {
    return 12 * hourMs;
  }
  return dayMs;
}

/**
 * Resolved x-axis domain for chart panels.
 *
 * - `relative`: rolling look-back ending now (existing epoch-aligned fill).
 * - `absolute`: `[startMs, selectedEndMs)` in UTC at `bucketMs`; only buckets
 *   before `effectiveEndMs` (= min(selected end, now)) are drawn, so a range
 *   that includes today never shows future hours as measured zero.
 * `effectiveEndMs` uses the response/render-time clock. The backend query captures
 * its own fixed half-open selected end before SQL; a bucket that elapsed while the
 * request was in flight remains valid when the client resolves this domain on response.
 */
export type MetricChartDomain =
  | ({ mode: "relative" } & MetricWindowRangeMs)
  | {
      mode: "absolute";
      startMs: number;
      selectedEndMs: number;
      effectiveEndMs: number;
      bucketMs: number;
    };

/** Resolves the chart domain for a period at `nowMs` (stores pass response time for validation). */
export function resolveChartDomain(
  period: MetricPeriod,
  nowMs: number,
): MetricChartDomain {
  if (period.mode === "relative") {
    return { mode: "relative", ...metricWindowRangeMs(period.window) };
  }
  const startMs = isoDateToUtcMs(period.startDate) ?? 0;
  const days = inclusiveDayCount(period.startDate, period.endDate) ?? 1;
  const selectedEndMs = startMs + days * dayMs;
  return {
    mode: "absolute",
    startMs,
    selectedEndMs,
    effectiveEndMs: Math.max(startMs, Math.min(selectedEndMs, nowMs)),
    bucketMs: customRangeBucketMs(days),
  };
}

/** Domain used when a panel has no period context (legacy relative default). */
export const defaultChartDomain: MetricChartDomain = {
  mode: "relative",
  ...metricWindowRangeMs(defaultMetricWindow),
};

/**
 * True when a server bucket lies outside the selected or currently elapsed custom range.
 * For custom responses, callers resolve the domain after awaiting the response: this
 * response-time clock is separate from the backend's fixed half-open query bound, so a
 * bucket that elapsed in flight is accepted while a genuinely future bucket is rejected.
 * Always false for rolling presets (their bounds move with the server clock).
 */
export function isBucketOutsidePeriod(
  bucket: Date | string,
  domain: MetricChartDomain,
): boolean {
  if (domain.mode === "relative") {
    return false;
  }
  const ms = new Date(bucket).getTime();
  return (
    Number.isNaN(ms) ||
    ms < domain.startMs ||
    ms >= domain.selectedEndMs ||
    ms >= domain.effectiveEndMs
  );
}
