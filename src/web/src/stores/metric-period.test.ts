import { describe, expect, it } from "vitest";
import {
  customRangeBucketMs,
  dayMs,
  formatMetricDateRange,
  inclusiveDayCount,
  isBucketOutsidePeriod,
  isCalendarDateDisabled,
  isHistoricalPeriod,
  isoDateToUtcMs,
  metricPeriodKey,
  metricPeriodLabel,
  metricPeriodQuery,
  msUntilNextUtcMidnight,
  parseStoredWindow,
  periodIncludesToday,
  promoteRecentRange,
  resolveChartDomain,
  sanitizeRecentRanges,
  utcTodayIso,
  validateMetricDateRange,
  type MetricDateRange,
  type MetricPeriod,
} from "./metric-period";

const today = "2026-10-19";
const hourMs = 3_600_000;

function range(startDate: string, endDate: string): MetricDateRange {
  return { startDate, endDate };
}

describe("validateMetricDateRange", () => {
  it.each([
    ["one date", "2026-09-01", "2026-09-01", 1],
    ["three dates", "2026-09-01", "2026-09-03", 3],
    ["September 1-30 (30 dates)", "2026-09-01", "2026-09-30", 30],
    ["leap-day crossing", "2024-02-28", "2024-03-02", 4],
    ["year crossing", "2025-12-30", "2026-01-02", 4],
    ["old range", "2020-01-01", "2020-01-10", 10],
    ["today is allowed", "2026-10-19", "2026-10-19", 1],
  ])("accepts %s", (_name, start, end, days) => {
    expect(validateMetricDateRange(start, end, today)).toEqual({
      valid: true,
      days,
    });
  });

  it.each([
    [
      "September 1 - October 1 is 31 dates",
      "2026-09-01",
      "2026-10-01",
      "too-long",
    ],
    ["reversed", "2026-09-03", "2026-09-01", "reversed"],
    ["future end", "2026-10-18", "2026-10-20", "future"],
    ["missing start", null, "2026-09-01", "missing-start"],
    ["missing end", "2026-09-01", null, "missing-end"],
    ["empty end", "2026-09-01", "", "missing-end"],
    ["non-real date", "2026-02-30", "2026-03-01", "invalid"],
    ["non-ISO shape", "2026-9-1", "2026-09-03", "invalid"],
    ["time suffix", "2026-09-01T00:00:00Z", "2026-09-03", "invalid"],
  ])("rejects %s without clamping", (_name, start, end, issue) => {
    const result = validateMetricDateRange(start, end, today);

    expect(result.valid).toBe(false);
    expect(result.valid === false && result.issue).toBe(issue);
  });

  it("counts inclusive dates across month and year boundaries", () => {
    expect(inclusiveDayCount("2026-09-30", "2026-10-01")).toBe(2);
    expect(inclusiveDayCount("2025-12-31", "2026-01-01")).toBe(2);
    expect(inclusiveDayCount("nope", "2026-01-01")).toBeNull();
  });

  it("parses strict UTC dates only", () => {
    expect(isoDateToUtcMs("2026-09-01")).toBe(Date.UTC(2026, 8, 1));
    expect(isoDateToUtcMs("0001-01-01")).toBe(
      Date.parse("0001-01-01T00:00:00.000Z"),
    );
    expect(isoDateToUtcMs("0099-12-31")).toBe(
      Date.parse("0099-12-31T00:00:00.000Z"),
    );
    expect(isoDateToUtcMs("0004-02-29")).toBe(
      Date.parse("0004-02-29T00:00:00.000Z"),
    );
    expect(isoDateToUtcMs("2024-02-29")).toBe(Date.UTC(2024, 1, 29));
    expect(isoDateToUtcMs("2000-02-29")).toBe(Date.UTC(2000, 1, 29));
    expect(isoDateToUtcMs("0000-01-01")).toBeNull();
    expect(isoDateToUtcMs("0001-02-29")).toBeNull();
    expect(isoDateToUtcMs("0099-02-29")).toBeNull();
    expect(isoDateToUtcMs("1900-02-29")).toBeNull();
    expect(isoDateToUtcMs("2026-02-30")).toBeNull();
    expect(isoDateToUtcMs("2026-02-29")).toBeNull();
  });
});

describe("recent range history", () => {
  const a = range("2026-09-01", "2026-09-03");
  const b = range("2026-09-04", "2026-09-06");
  const c = range("2026-09-07", "2026-09-09");
  const d = range("2026-09-10", "2026-09-12");

  it("keeps newest first and evicts beyond three (A/B/C/D -> D/C/B)", () => {
    const history = [a, b, c, d].reduce<MetricDateRange[]>(
      (current, entry) => promoteRecentRange(current, entry),
      [],
    );

    expect(history).toEqual([d, c, b]);
  });

  it("promotes a reused range without duplicating it (B -> B/D/C)", () => {
    const history = promoteRecentRange([d, c, b], b);

    expect(history).toEqual([b, d, c]);
  });

  it("deduplicates only by exact start and end dates", () => {
    const history = promoteRecentRange([a], range("2026-09-01", "2026-09-04"));

    expect(history).toHaveLength(2);
  });

  it("drops malformed, reversed, overwide, future and unknown-shaped entries", () => {
    const stored: unknown = [
      a,
      { startDate: "2026-09-03", endDate: "2026-09-01" },
      { startDate: "2026-01-01", endDate: "2026-03-01" },
      { startDate: "2026-10-19", endDate: "2026-10-25" },
      { startDate: 5, endDate: "2026-09-01" },
      "2026-09-01",
      null,
      a,
      b,
      c,
      d,
    ];

    expect(sanitizeRecentRanges(stored, today)).toEqual([a, b, c]);
    expect(sanitizeRecentRanges({ not: "an array" }, today)).toEqual([]);
  });
});

describe("period helpers", () => {
  const relative: MetricPeriod = { mode: "relative", window: "7d" };
  const absolute: MetricPeriod = {
    mode: "absolute",
    startDate: "2026-09-01",
    endDate: "2026-09-03",
  };

  it("sends window XOR dates", () => {
    expect(metricPeriodQuery(relative)).toEqual({ window: "7d" });
    expect(metricPeriodQuery(absolute)).toEqual({
      startDate: "2026-09-01",
      endDate: "2026-09-03",
    });
  });

  it("keys periods by mode and exact dates", () => {
    expect(metricPeriodKey(relative)).not.toBe(metricPeriodKey(absolute));
    expect(metricPeriodKey(absolute)).toBe("r:2026-09-01:2026-09-03");
  });

  it("restores only known rolling tokens", () => {
    expect(parseStoredWindow("72h")).toBe("72h");
    expect(parseStoredWindow("13d")).toBe("24h");
    expect(parseStoredWindow(null)).toBe("24h");
  });

  it("labels custom periods with full dates, year and UTC", () => {
    // Intl pads the en dash with thin/narrow no-break spaces; compare on normalized spaces.
    const label = (value: MetricDateRange) =>
      formatMetricDateRange(value, "en-US").replace(
        /[\u2009\u202f\u00a0]/g,
        " ",
      );
    expect(label(range("2026-09-01", "2026-09-03"))).toBe(
      "Sep 1 – 3, 2026 · UTC",
    );
    expect(label(range("2026-09-01", "2026-09-01"))).toBe("Sep 1, 2026 · UTC");
    const crossYear = label(range("2025-12-30", "2026-01-02"));
    expect(crossYear).toContain("2025");
    expect(crossYear).toContain("2026");
    expect(crossYear.endsWith("UTC")).toBe(true);
    expect(metricPeriodLabel(relative)).toBe("Last 7 days");
  });

  it("classifies live and historical custom periods on the UTC date", () => {
    const live: MetricPeriod = { ...absolute, endDate: today };

    expect(isHistoricalPeriod(absolute, today)).toBe(true);
    expect(isHistoricalPeriod(live, today)).toBe(false);
    expect(periodIncludesToday(live, today)).toBe(true);
    expect(isHistoricalPeriod(relative, today)).toBe(false);
    // Midnight advances "today" but never moves the selected dates.
    expect(isHistoricalPeriod(live, "2026-10-20")).toBe(true);
    expect(live.startDate).toBe("2026-09-01");
  });

  it("derives the UTC date and time to next midnight independent of local zone", () => {
    const lateEvening = Date.UTC(2026, 9, 19, 23, 30);

    expect(utcTodayIso(lateEvening)).toBe("2026-10-19");
    expect(msUntilNextUtcMidnight(lateEvening)).toBe(30 * 60_000);
    expect(utcTodayIso(Date.UTC(2026, 9, 20, 0, 0))).toBe("2026-10-20");
  });
});

describe("custom bucket policy", () => {
  it.each([
    [1, 1],
    [2, 2],
    [3, 2],
    [4, 6],
    [7, 6],
    [8, 12],
    [14, 12],
    [15, 24],
    [30, 24],
  ])("%i selected date(s) use %i hour buckets", (days, hours) => {
    expect(customRangeBucketMs(days)).toBe(hours * hourMs);
  });

  it("resolves a historical absolute domain with explicit bounds", () => {
    const now = Date.UTC(2026, 9, 19, 12);
    const domain = resolveChartDomain(
      { mode: "absolute", startDate: "2026-09-01", endDate: "2026-09-03" },
      now,
    );

    expect(domain).toEqual({
      mode: "absolute",
      startMs: Date.UTC(2026, 8, 1),
      selectedEndMs: Date.UTC(2026, 8, 4),
      effectiveEndMs: Date.UTC(2026, 8, 4),
      bucketMs: 2 * hourMs,
    });
  });

  it("caps the effective end at now for a range including today, keeping the selection fixed", () => {
    const now = Date.UTC(2026, 9, 19, 5, 30);
    const domain = resolveChartDomain(
      { mode: "absolute", startDate: "2026-10-18", endDate: "2026-10-19" },
      now,
    );

    expect(domain.mode === "absolute" && domain.effectiveEndMs).toBe(now);
    expect(domain.mode === "absolute" && domain.selectedEndMs).toBe(
      Date.UTC(2026, 9, 20),
    );
  });

  it("allows an empty elapsed domain at exactly UTC midnight for a today-only range", () => {
    const midnight = Date.UTC(2026, 9, 19);
    const domain = resolveChartDomain(
      { mode: "absolute", startDate: "2026-10-19", endDate: "2026-10-19" },
      midnight,
    );

    expect(domain.mode === "absolute" && domain.effectiveEndMs).toBe(
      domain.mode === "absolute" ? domain.startMs : -1,
    );
  });

  it("flags buckets outside selected dates or the elapsed portion for custom ranges", () => {
    const startMs = Date.UTC(2026, 8, 1);
    const domain = resolveChartDomain(
      { mode: "absolute", startDate: "2026-09-01", endDate: "2026-09-01" },
      startMs + 12 * hourMs,
    );

    expect(isBucketOutsidePeriod(new Date(startMs), domain)).toBe(false);
    expect(isBucketOutsidePeriod(new Date(startMs + 11 * hourMs), domain)).toBe(
      false,
    );
    expect(isBucketOutsidePeriod(new Date(startMs + 12 * hourMs), domain)).toBe(
      true,
    );
    expect(isBucketOutsidePeriod(new Date(startMs + dayMs - 1), domain)).toBe(
      true,
    );
    expect(isBucketOutsidePeriod(new Date(startMs + dayMs), domain)).toBe(true);
    expect(isBucketOutsidePeriod(new Date(startMs - hourMs), domain)).toBe(
      true,
    );
    expect(
      isBucketOutsidePeriod(
        new Date(0),
        resolveChartDomain({ mode: "relative", window: "24h" }, 0),
      ),
    ).toBe(false);
  });
});

describe("isCalendarDateDisabled", () => {
  it("disables future UTC dates but allows today", () => {
    expect(isCalendarDateDisabled("2026-10-19", today, null)).toBe(false);
    expect(isCalendarDateDisabled("2026-10-20", today, null)).toBe(true);
  });

  it("allows any past date when no anchor is pending (new anchor anywhere)", () => {
    expect(isCalendarDateDisabled("2020-01-01", today, null)).toBe(false);
  });

  it("limits a pending pick to 30 inclusive dates in either direction", () => {
    expect(isCalendarDateDisabled("2026-09-01", today, "2026-09-01")).toBe(
      false,
    );
    // 29 days later = 30 dates inclusive: allowed; 30 days later = 31 dates: blocked.
    expect(isCalendarDateDisabled("2026-09-30", today, "2026-09-01")).toBe(
      false,
    );
    expect(isCalendarDateDisabled("2026-10-01", today, "2026-09-01")).toBe(
      true,
    );
    // Reverse selection mirrors the rule.
    expect(isCalendarDateDisabled("2026-08-03", today, "2026-09-01")).toBe(
      false,
    );
    expect(isCalendarDateDisabled("2026-08-02", today, "2026-09-01")).toBe(
      true,
    );
  });

  it("still blocks future dates while an anchor is pending", () => {
    expect(isCalendarDateDisabled("2026-10-20", today, "2026-10-10")).toBe(
      true,
    );
  });
});
