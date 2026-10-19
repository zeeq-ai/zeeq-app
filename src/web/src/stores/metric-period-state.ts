import { ref } from "vue";
import { useLocalStorage } from "@vueuse/core";
import {
  defaultMetricWindow,
  isSamePeriod,
  parseStoredWindow,
  promoteRecentRange,
  relativePeriod,
  sanitizeRecentRanges,
  utcTodayIso,
  type MetricDateRange,
  type MetricPeriod,
  type MetricWindowToken,
} from "@/stores/metric-period";

const savedWindowKey = "zeeq:metrics-window";
const recentRangesKey = "zeeq:metrics-recent-ranges:v1";

/**
 * Builds the shared period/preference state.
 *
 * - `savedWindow` (localStorage, existing key, plain token string): the last
 *   *rolling preset*; unknown values fall back to 24h.
 * - `recentRanges` (localStorage, versioned key): up to three applied custom
 *   ranges, validated on every read so tampered or stale entries never reach a
 *   query. Cross-tab changes arrive through VueUse's storage listener.
 * - `period` (memory only): the applied period shared by Home and Library for
 *   the current SPA page session. It starts from the saved preset, so a full
 *   reload or new visit returns to the last rolling preset; an applied custom
 *   range is never persisted as the active period.
 *
 * @param nowMs - Clock for validating stored ranges (injectable for tests).
 */
export function createMetricPeriodState(nowMs: () => number = Date.now) {
  const savedWindow = useLocalStorage<MetricWindowToken>(
    savedWindowKey,
    defaultMetricWindow,
    {
      serializer: { read: parseStoredWindow, write: (value) => value },
    },
  );

  const recentRanges = useLocalStorage<MetricDateRange[]>(recentRangesKey, [], {
    serializer: {
      read: (raw) => {
        try {
          return sanitizeRecentRanges(JSON.parse(raw), utcTodayIso(nowMs()));
        } catch {
          return [];
        }
      },
      write: (value) => JSON.stringify(value),
    },
  });

  const period = ref<MetricPeriod>(relativePeriod(savedWindow.value));

  /** Remembers a committed custom range (promotes on reuse); never touches the active period. */
  function recordRecentRange(range: MetricDateRange) {
    recentRanges.value = promoteRecentRange(recentRanges.value, range);
  }

  /**
   * Commits a period. A preset is persisted as the saved rolling window; a
   * custom range only enters history, so the saved preset is what a reload
   * restores. History is recorded even when the range is already active
   * (promotion) and regardless of whether the follow-up fetch succeeds.
   */
  function applyPeriod(next: MetricPeriod) {
    if (next.mode === "relative") {
      savedWindow.value = next.window;
    } else {
      recordRecentRange(next);
    }
    if (!isSamePeriod(period.value, next)) {
      period.value = next;
    }
  }

  return { period, savedWindow, recentRanges, applyPeriod, recordRecentRange };
}

export type MetricPeriodState = ReturnType<typeof createMetricPeriodState>;

/** Process-wide instance: Home and Library metrics share one applied period and history. */
export const sharedMetricPeriodState = createMetricPeriodState();
