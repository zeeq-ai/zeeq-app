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
 * Drives periodic refresh of the metrics dashboard.
 *
 * Wraps a caller-supplied `refresh` (Home reloads the active tab's panels) in a
 * pausable interval whose cadence follows a persisted mode. The timer skips
 * ticks while `paused` (the user is interacting, e.g. dragging a dataZoom
 * brush) so a refresh never yanks the chart out from under them, and never
 * overlaps an in-flight refresh. `refreshNow` powers the manual button, and
 * `lastUpdatedAt` feeds the "updated Xs ago" label.
 *
 * @param refresh - Reloads whatever the active tab needs; awaited per tick.
 */
export function useMetricsAutoRefresh(refresh: () => Promise<void>) {
  const mode = useLocalStorage<AutoRefreshMode>(
    "metrics-auto-refresh-mode",
    "slow",
  );
  const lastUpdatedAt = ref<Date | null>(null);
  const refreshing = ref(false);
  const paused = ref(false);

  const intervalMs = computed(() => autoRefreshIntervalsMs[mode.value]);

  /** Runs one refresh, guarding against overlap and stamping the update time. */
  async function refreshNow() {
    if (refreshing.value) {
      return;
    }
    refreshing.value = true;
    try {
      await refresh();
      lastUpdatedAt.value = new Date();
    } finally {
      refreshing.value = false;
    }
  }

  const { pause, resume } = useIntervalFn(
    () => {
      if (!paused.value) {
        void refreshNow();
      }
    },
    intervalMs,
    { immediate: false },
  );

  // Enable the timer for a cadence, disable it entirely when the mode is off.
  watch(
    mode,
    (value) => {
      if (value === "off") {
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
