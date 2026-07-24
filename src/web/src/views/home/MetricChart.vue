<template>
  <!--
  Single sized root so ECharts has explicit dimensions and native pointer
  events (used by the dashboard's pause-on-interact) fall through to it.
  Renders the `empty` slot instead of the chart when a panel has no data.
  -->
  <div class="w-full" :style="{ height }">
    <slot v-if="empty" name="empty">
      <div class="flex h-full items-center justify-center text-sm text-dimmed">
        No data for this window.
      </div>
    </slot>

    <!-- vue-echarts is imported here only; theme tracks the app color mode. -->
    <VChart
      v-else
      :option="chartOption"
      :theme="theme"
      :loading="showLoadingMask"
      :loading-options="loadingOptions"
      autoresize
    />
  </div>
</template>

<script setup lang="ts">
import VChart from "vue-echarts";
import { useColorMode } from "@vueuse/core";
import type { EChartsOption } from "echarts";
// Side-effect: register the renderer/charts/components this dashboard uses.
import "@/views/home/echarts-setup";

const props = withDefaults(
  defineProps<{
    /** Fully-built ECharts option; panels compute this from store DTOs. */
    option: EChartsOption;
    /** Shows the ECharts spinner overlay while the backing panel loads. */
    loading?: boolean;
    /** Renders the empty slot instead of the chart. */
    empty?: boolean;
    /** Explicit height — ECharts needs a sized container to render. */
    height?: string;
  }>(),
  {
    loading: false,
    empty: false,
    height: "24rem",
  },
);

const colorMode = useColorMode();

/**
 * Auto-refresh polls rebuild each panel's option (new bucket window, updated
 * totals) and hand it to vue-echarts as a brand-new object every ~45s-3m.
 * vue-echarts merges that in via `setOption(option, { notMerge: false })`
 * (see the `id`-stability note in chart-options.ts) rather than a full
 * replace — confirmed by instrumenting the live chart instance, which is not
 * the culprit here.
 *
 * The actual cause: our bar `series.data` arrays are bare `number[]`, with
 * no stable per-point `name`/`id`. ECharts' data differ tracks identity by
 * `name` (see the transition-animation guide in the ECharts handbook); with
 * none set, it can't match "this bar" across two setOption calls, so it
 * treats every bucket as a brand-new data point each poll — removing the old
 * bar and adding a new one — which plays the *entrance* animation
 * (`animationDuration`, ~1s) on every refresh, not the update transition
 * (`animationDurationUpdate`). Confirmed by sampling canvas pixels across a
 * manual refresh: the bar collapses to ~0 and climbs back over ~950ms,
 * matching `animationDuration`'s default — zeroing only
 * `animationDurationUpdate` (tried first) had no effect on this.
 *
 * Rather than restructure every builder's series data into named points,
 * force zero duration for *both* entrance and update animation from the
 * second render onward — the first paint still gets its entrance animation
 * once, and every poll after that is instant regardless of which animation
 * ECharts thinks it's playing.
 */
let hasRenderedOnce = false;
const chartOption = computed<EChartsOption>(() => {
  const option: EChartsOption = {
    animationDurationUpdate: 0,
    ...props.option,
  };
  if (hasRenderedOnce) {
    option.animationDuration = 0;
  }
  hasRenderedOnce = true;
  return option;
});

/**
 * ECharts' native loading mask (spinner + dimmed overlay) is right for the
 * panel's first fetch — there's nothing to show yet. But `loading` also goes
 * true on every background auto-refresh poll (store's `run()` wraps every
 * fetch, first load or not — see metrics-store.ts), and toggling the mask on
 * a chart that already has data flashes the dim overlay over already-correct
 * bars every single poll. At the default 45s cadence it's easy to miss; at a
 * fast interval it reads as a constant flicker. Only the first load has nothing
 * on screen to protect, so gate the mask on that: once the panel's first
 * fetch completes, never show it again — later polls update the chart data
 * silently (the "Updated Xs ago" label is what tells the user a refresh
 * happened).
 */
const hasLoadedOnce = ref(false);
watch(
  () => props.loading,
  (isLoading, wasLoading) => {
    if (wasLoading && !isLoading) {
      hasLoadedOnce.value = true;
    }
  },
);
const showLoadingMask = computed(() => props.loading && !hasLoadedOnce.value);

/**
 * "walden" and "walden-dark" are the custom light/dark chart themes registered
 * in echarts-setup.ts (same palette, dark swaps in a subtle split-line color).
 * Mirrors the app's `colorMode.value === "dark"` convention.
 */
const theme = computed(() =>
  colorMode.value === "dark" ? "walden-dark" : "walden",
);

/** Transparent mask so the spinner does not flash a white box in dark mode. */
const loadingOptions = computed(() => ({
  text: "",
  maskColor:
    colorMode.value === "dark"
      ? "rgba(9, 9, 11, 0.6)"
      : "rgba(255, 255, 255, 0.6)",
  color: "#6366f1",
}));
</script>
