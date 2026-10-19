<template>
  <!--
  Single sized root so ECharts has explicit dimensions and native pointer
  events (used by the dashboard's pause-on-interact) fall through to it.
  Renders the `empty` slot instead of the chart when a panel has no data — but
  loading takes precedence: a panel that is (re)loading with nothing to show
  (first load, or data cleared by a period/filter change) shows a loading state,
  never "empty", so missing data is not presented as proven zero activity.
  -->
  <div class="w-full" :style="{ height }">
    <div
      v-if="loading && empty"
      role="status"
      class="flex h-full items-center justify-center gap-2 text-sm text-dimmed"
    >
      <UIcon name="i-hugeicons-loading-03" class="size-4 animate-spin" />
      Loading…
    </div>

    <slot v-else-if="empty" name="empty">
      <div class="flex h-full items-center justify-center text-sm text-dimmed">
        No telemetry returned for this period.
      </div>
    </slot>

    <!-- vue-echarts is imported here only; theme tracks the app color mode. -->
    <VChart
      v-else
      :option="chartOption"
      :theme="theme"
      :loading="showLoadingMask"
      :loading-options="loadingOptions"
      @click="emits('pointClick', $event)"
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

type LegendSize = "sm" | "md" | "lg";

/**
 * A chart's plot must reserve the same horizontal room as its legend column.
 * Keep the values centralized so dashboard panels use a consistent rhythm
 * instead of encoding arbitrary pixel measurements at each call site.
 */
const legendLayouts: Record<LegendSize, { width: number; gridRight: number }> =
  {
    sm: { width: 140, gridRight: 168 },
    md: { width: 180, gridRight: 208 },
    lg: { width: 220, gridRight: 248 },
  };

const props = withDefaults(
  defineProps<{
    /** Fully-built ECharts option; panels compute this from store DTOs. */
    option: EChartsOption;
    /** Panel is loading: spinner overlay on first paint, or a loading state when there is nothing to show. */
    loading?: boolean;
    /** Renders the empty slot instead of the chart. */
    empty?: boolean;
    /** Explicit height — ECharts needs a sized container to render. */
    height?: string;
    /** Opt-in fixed legend column size for charts with a vertical legend. */
    legendSize?: LegendSize;
  }>(),
  {
    loading: false,
    empty: false,
    height: "24rem",
  },
);

const emits = defineEmits<{ pointClick: [event: unknown] }>();

const colorMode = useColorMode();

/**
 * Auto-refresh polls hand vue-echarts a new option object every ~45s-3m;
 * it merges via `setOption(option, { notMerge: false })`. Each bar's stable
 * `id`+`name` (chart-options.ts) lets ECharts' differ distinguish "bucket
 * picked up more value" (update transition, `animationDurationUpdate`) from
 * "bucket entered/exited the window" (enter/exit, `animationDuration`).
 * Update duration shortened from ECharts' ~1s default so polls feel snappy;
 * entrance keeps the default for a proper first-paint reveal.
 */
const chartOption = computed<EChartsOption>(() => {
  const option = {
    animationDurationUpdate: 300,
    ...props.option,
  };
  const legendSize = props.legendSize;

  // Multi-grid and multi-legend charts manage their own geometry. The
  // dashboard's time-series charts use one of each, which this layout keeps
  // aligned without asking each panel to duplicate pixel values.
  if (
    !legendSize ||
    !props.option.legend ||
    Array.isArray(props.option.legend) ||
    Array.isArray(props.option.grid)
  ) {
    return option;
  }

  const layout = legendLayouts[legendSize];
  return {
    ...option,
    legend: {
      ...props.option.legend,
      align: "right",
      width: layout.width,
    },
    grid: {
      ...props.option.grid,
      right: layout.gridRight,
    },
  };
});

/**
 * `loading` goes true on every poll, not just the first fetch (store's
 * `run()` wraps every fetch — metrics-store.ts). ECharts' native loading
 * mask is only wanted for the first (nothing on screen yet); gate it so
 * later polls update silently instead of flashing the dim overlay over
 * already-correct bars — the "Updated Xs ago" label covers that signal.
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
