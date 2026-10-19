import { onMounted, onUnmounted, ref } from "vue";
import { msUntilNextUtcMidnight, utcTodayIso } from "@/stores/metric-period";

/**
 * Reactive UTC "today" (`yyyy-MM-dd`) that re-evaluates at each UTC midnight and when the
 * tab becomes visible again (timers are throttled in background tabs).
 *
 * Drives the calendar's max date, the "Today is partial" caption and the
 * historical-vs-live refresh policy. A selected range never moves when this changes;
 * only whether it counts as completed does.
 *
 * @param nowMs - Clock (injectable for tests).
 */
export function useUtcToday(nowMs: () => number = Date.now) {
  const todayUtc = ref(utcTodayIso(nowMs()));
  let timer: ReturnType<typeof setTimeout> | undefined;

  function refresh() {
    todayUtc.value = utcTodayIso(nowMs());
  }

  function schedule() {
    clearTimeout(timer);
    // +1s so the callback lands just after midnight even with small timer drift.
    timer = setTimeout(
      () => {
        refresh();
        schedule();
      },
      msUntilNextUtcMidnight(nowMs()) + 1000,
    );
  }

  function onVisibilityChange() {
    if (document.visibilityState === "visible") {
      refresh();
      schedule();
    }
  }

  onMounted(() => {
    refresh();
    schedule();
    document.addEventListener("visibilitychange", onVisibilityChange);
  });

  onUnmounted(() => {
    clearTimeout(timer);
    document.removeEventListener("visibilitychange", onVisibilityChange);
  });

  return todayUtc;
}
