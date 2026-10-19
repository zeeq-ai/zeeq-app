import { beforeEach, describe, expect, it, vi } from "vitest";
import { nextTick } from "vue";
import {
  absolutePeriod,
  relativePeriod,
  type MetricDateRange,
} from "@/stores/metric-period";
import { createMetricPeriodState } from "./metric-period-state";

const now = Date.UTC(2026, 9, 19, 12);
const windowKey = "zeeq:metrics-window";
const rangesKey = "zeeq:metrics-recent-ranges:v1";

/**
 * In-memory Storage: Node's experimental global `localStorage` shadows the DOM one under
 * vitest, so the persistence tests install a deterministic stand-in on both globals.
 */
function installMemoryStorage() {
  const data = new Map<string, string>();
  const storage = {
    get length() {
      return data.size;
    },
    clear: () => data.clear(),
    getItem: (key: string) => data.get(key) ?? null,
    key: (index: number) => [...data.keys()][index] ?? null,
    removeItem: (key: string) => void data.delete(key),
    setItem: (key: string, value: string) => void data.set(key, value),
  };
  vi.stubGlobal("localStorage", storage);
  vi.stubGlobal(
    "window",
    Object.assign(Object.create(globalThis), { localStorage: storage }),
  );
}

function range(startDate: string, endDate: string): MetricDateRange {
  return { startDate, endDate };
}

/** A fresh state instance stands in for a full page reload (module state is per page). */
function newPage() {
  return createMetricPeriodState(() => now);
}

describe("createMetricPeriodState persistence", () => {
  beforeEach(() => {
    installMemoryStorage();
  });

  it("starts on 24h and restores a saved rolling preset", () => {
    expect(newPage().period.value).toEqual(relativePeriod("24h"));

    localStorage.setItem(windowKey, "7d");

    expect(newPage().period.value).toEqual(relativePeriod("7d"));
  });

  it("recovers from an unknown saved token", () => {
    localStorage.setItem(windowKey, "13d");

    expect(newPage().period.value).toEqual(relativePeriod("24h"));
  });

  it("recovers from malformed or hand-edited history", () => {
    localStorage.setItem(rangesKey, "{not json");
    expect(newPage().recentRanges.value).toEqual([]);

    localStorage.setItem(
      rangesKey,
      JSON.stringify([
        range("2026-09-03", "2026-09-01"),
        range("2026-09-01", "2026-09-03"),
        { startDate: 1 },
      ]),
    );
    expect(newPage().recentRanges.value).toEqual([
      range("2026-09-01", "2026-09-03"),
    ]);
  });

  it("persists a preset as the saved window", async () => {
    const state = newPage();

    state.applyPeriod(relativePeriod("72h"));
    await nextTick();

    expect(localStorage.getItem(windowKey)).toBe("72h");
    expect(state.period.value).toEqual(relativePeriod("72h"));
  });

  it("applies a custom range in memory and remembers it without overwriting the saved preset", async () => {
    const state = newPage();
    state.applyPeriod(relativePeriod("7d"));
    state.applyPeriod(absolutePeriod(range("2026-09-01", "2026-09-03")));
    await nextTick();

    expect(state.period.value).toEqual(
      absolutePeriod(range("2026-09-01", "2026-09-03")),
    );
    expect(localStorage.getItem(windowKey)).toBe("7d");
    expect(JSON.parse(localStorage.getItem(rangesKey) ?? "[]")).toEqual([
      range("2026-09-01", "2026-09-03"),
    ]);
  });

  it("returns to the last rolling preset after a reload while keeping recent ranges", async () => {
    const first = newPage();
    first.applyPeriod(relativePeriod("14d"));
    first.applyPeriod(absolutePeriod(range("2026-09-01", "2026-09-03")));
    await nextTick();

    const afterReload = newPage();

    expect(afterReload.period.value).toEqual(relativePeriod("14d"));
    expect(afterReload.recentRanges.value).toEqual([
      range("2026-09-01", "2026-09-03"),
    ]);
  });

  it("shares one applied period through the same state instance (Home <-> Library navigation)", () => {
    const state = newPage();

    state.applyPeriod(absolutePeriod(range("2026-09-01", "2026-09-03")));

    // Both stores read the same ref, so a view mounted later sees the active custom range.
    expect(state.period.value.mode).toBe("absolute");
  });

  it("keeps three recent ranges newest first, promoting reuse without duplicates", async () => {
    const state = newPage();
    const [a, b, c, d] = [
      range("2026-09-01", "2026-09-02"),
      range("2026-09-03", "2026-09-04"),
      range("2026-09-05", "2026-09-06"),
      range("2026-09-07", "2026-09-08"),
    ];

    for (const entry of [a, b, c, d]) {
      state.applyPeriod(absolutePeriod(entry));
    }
    expect(state.recentRanges.value).toEqual([d, c, b]);

    state.applyPeriod(absolutePeriod(b));
    await nextTick();

    expect(state.recentRanges.value).toEqual([b, d, c]);
    expect(JSON.parse(localStorage.getItem(rangesKey) ?? "[]")).toEqual([
      b,
      d,
      c,
    ]);
  });

  it("does not let presets evict history, and a findings-local use updates history only", () => {
    const state = newPage();
    const a = range("2026-09-01", "2026-09-02");
    state.applyPeriod(absolutePeriod(a));

    state.applyPeriod(relativePeriod("1h"));
    expect(state.recentRanges.value).toEqual([a]);

    const local = range("2026-08-01", "2026-08-03");
    state.recordRecentRange(local);

    expect(state.recentRanges.value).toEqual([local, a]);
    expect(state.period.value).toEqual(relativePeriod("1h"));
  });

  it("keeps the applied period when its entry has been evicted from history", () => {
    const state = newPage();
    const active = range("2026-09-01", "2026-09-02");
    state.applyPeriod(absolutePeriod(active));

    for (const entry of [
      range("2026-09-03", "2026-09-04"),
      range("2026-09-05", "2026-09-06"),
      range("2026-09-07", "2026-09-08"),
    ]) {
      state.recordRecentRange(entry);
    }

    expect(state.recentRanges.value).not.toContainEqual(active);
    expect(state.period.value).toEqual(absolutePeriod(active));
  });
});
