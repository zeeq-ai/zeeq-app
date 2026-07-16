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
      :option="option"
      :theme="theme"
      :loading="loading"
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

withDefaults(
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
