<template>
  <!--
  Performance tab: review-duration p50/p95/p99 (UI-8) with a
  duration-vs-tokens scatter for the selected dashboard window.
  -->
  <div class="flex flex-col gap-4">
    <!-- Side by side on larger screens; stacked on narrow viewports. -->
    <div class="grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Review duration percentiles</span>
        </template>
        <MetricChart
          :option="percentileOption"
          :loading="loadingPercentiles"
          :empty="durationPercentiles.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Duration vs. tokens</span>
        </template>
        <MetricChart
          :option="scatterOption"
          :loading="loadingScatter"
          :empty="scatterWithTokens === 0"
        />
      </UCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import type {
  MetricPercentilePoint,
  MetricScatterPoint,
} from "@/api/generated";
import MetricChart from "./MetricChart.vue";
import { percentileLinesOption, tokenScatterOption } from "./chart-options";

const props = defineProps<{
  durationPercentiles: MetricPercentilePoint[];
  durationScatter: MetricScatterPoint[];
  loadingPercentiles: boolean;
  loadingScatter: boolean;
}>();

/** UI-8: p50/p95/p99 line chart over the window. */
const percentileOption = computed(() =>
  percentileLinesOption(props.durationPercentiles),
);

/** UI-8: duration-vs-tokens scatter (samples missing a token count are dropped). */
const scatterOption = computed(() => tokenScatterOption(props.durationScatter));

/** Count of samples that actually carry a token value — drives the empty state. */
const scatterWithTokens = computed(
  () => props.durationScatter.filter((point) => point.tokens !== null).length,
);
</script>
