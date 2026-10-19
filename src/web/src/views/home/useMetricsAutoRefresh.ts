import { computed, onUnmounted, ref, watch, type Ref } from "vue";
import { useIntervalFn, useLocalStorage } from "@vueuse/core";

/** Auto-refresh cadences offered on the dashboard. */
export type AutoRefreshMode = "off" | "fast" | "slow";

/** Cadence in milliseconds per mode; `off` disables the timer entirely. */
export const autoRefreshIntervalsMs: Record<AutoRefreshMode, number> = {
  off: 0,
  fast: 45_000,
  slow: 180_000,
};

/** `{ label, value }` items for the refresh-mode selector. */
export const autoRefreshItems: { label: string; value: AutoRefreshMode }[] = [
  { label: "Off", value: "off" },
  { label: "Every 45s", value: "fast" },
  { label: "Every 3m", value: "slow" },
];

/**
 * Serializes batches of reloads and coalesces requests that arrive meanwhile.
 *
 * At most one batch runs at a time (batches never overlap). A request made while a
 * batch is in flight is *queued* as one trailing batch that starts only after the
 * running batch has fully settled — success or failure — and that reads the latest
 * state when it starts. Any number of requests during that wait share the same
 * trailing batch, so the latest intent is never skipped and never duplicated.
 *
 * Exported separately from the composable so the race behavior is unit-testable
 * without timers or Vue lifecycles.
 *
 * @param execute - Runs one full batch; the returned promise must settle only when
 *   every request in the batch has settled.
 */
export function createCoalescingRunner(execute: () => Promise<void>) {
  let inFlight: Promise<void> | null = null;
  let trailing: Promise<void> | null = null;

  function start(): Promise<void> {
    const batch: Promise<void> = execute().finally(() => {
      if (inFlight === batch) {
        inFlight = null;
      }
    });
    inFlight = batch;
    return batch;
  }

  /** Runs now, or queues one trailing batch behind the running one. */
  function request(): Promise<void> {
    // Idle means *nothing running and nothing queued*: between the running batch clearing
    // `inFlight` and its queued successor starting there is a microtask gap in which a
    // request must join the queued batch, not start a third, overlapping one.
    if (inFlight === null && trailing === null) {
      return start();
    }
    if (trailing === null && inFlight !== null) {
      const previous = inFlight;
      const queued: Promise<void> = previous
        .then(
          () => undefined,
          () => undefined,
        )
        .then(() => {
          trailing = null;
          return start();
        });
      trailing = queued;
    }
    return trailing ?? start();
  }

  /** Runs only when idle (timer ticks): a busy tick is dropped rather than queued. */
  function requestIfIdle(): Promise<void> | null {
    return inFlight === null && trailing === null ? start() : null;
  }

  return {
    request,
    requestIfIdle,
    isBusy: () => inFlight !== null || trailing !== null,
  };
}

/**
 * Drives refresh of the metrics dashboard.
 *
 * Wraps a caller-supplied `refresh` (Home reloads the active tab's panels) with a
 * coalescing runner, a persisted cadence, and a pausable interval. `refreshNow`
 * (manual button, period/filter/tab changes) always results in a batch that sees the
 * latest state — queued behind an in-flight batch rather than dropped. Timer ticks,
 * by contrast, are skipped while a batch runs or the user is interacting.
 *
 * The saved cadence is never rewritten: when `suspended` is true (the date editor is
 * open, or a completed historical range has nothing new to fetch) the *effective*
 * timer is stopped and resumes unchanged afterwards.
 *
 * @param refresh - Reloads whatever the active tab needs; awaited per batch.
 * @param suspended - True to stop the effective timer without changing `mode`.
 */
export function useMetricsAutoRefresh(
  refresh: () => Promise<void>,
  suspended: Ref<boolean> = ref(false),
) {
  const mode = useLocalStorage<AutoRefreshMode>(
    "metrics-auto-refresh-mode",
    "slow",
  );
  const lastUpdatedAt = ref<Date | null>(null);
  const refreshing = ref(false);
  const paused = ref(false);

  const intervalMs = computed(() => autoRefreshIntervalsMs[mode.value]);

  const runner = createCoalescingRunner(async () => {
    await refresh();
    lastUpdatedAt.value = new Date();
  });

  // `refreshing` mirrors the runner (true across a queued trailing batch too), re-read
  // whenever a batch is requested or settles.
  const syncRefreshing = () => {
    refreshing.value = runner.isBusy();
  };

  /** Manual/selection-driven reload: runs now or as the single coalesced trailing batch. */
  function refreshNow(): Promise<void> {
    const batch = runner.request();
    syncRefreshing();
    void batch.then(syncRefreshing, syncRefreshing);
    return batch;
  }

  const { pause, resume } = useIntervalFn(
    () => {
      if (!paused.value) {
        const batch = runner.requestIfIdle();
        if (batch) {
          syncRefreshing();
          void batch.then(syncRefreshing, syncRefreshing);
        }
      }
    },
    intervalMs,
    { immediate: false },
  );

  // Enable the timer for a cadence; stop it when off or suspended (saved mode untouched).
  watch(
    [mode, suspended],
    ([value, isSuspended]) => {
      if (value === "off" || isSuspended) {
        pause();
      } else {
        resume();
      }
    },
    { immediate: true },
  );

  /** Pauses the cadence while the user interacts with a chart. */
  function beginInteraction() {
    paused.value = true;
  }

  /** Resumes the cadence once interaction ends. */
  function endInteraction() {
    paused.value = false;
  }

  onUnmounted(() => pause());

  return {
    mode,
    lastUpdatedAt,
    refreshing,
    refreshNow,
    beginInteraction,
    endInteraction,
  };
}
