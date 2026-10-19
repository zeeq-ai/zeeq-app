<template>
  <!-- Library root dashboard: shared reporting period + four equal metric panels scoped to one library. -->
  <div class="flex h-full min-h-0 flex-col overflow-y-auto">
    <div
      class="flex min-h-11.25 shrink-0 flex-wrap items-center justify-between gap-x-3 gap-y-2 border-b border-default px-3 py-1.5"
    >
      <div class="min-w-0">
        <div class="truncate text-sm font-medium">Library metrics</div>
      </div>

      <div class="flex items-center gap-2">
        <UButton
          icon="i-hugeicons-refresh"
          color="neutral"
          variant="ghost"
          :loading="refreshing"
          aria-label="Refresh library metrics"
          @click="emits('refresh')"
        />
        <MetricsWindowSelect
          :period="period"
          :recent-ranges="recentRanges"
          :today-utc="todayUtc"
          @select="(value) => emits('selectPeriod', value)"
        />
      </div>
    </div>

    <div class="flex flex-col gap-3 p-3">
      <!-- Full UTC dates and the generic older-telemetry note for custom ranges. -->
      <MetricPeriodNotice :period="period" :today-utc="todayUtc" />

      <UAlert
        v-if="error"
        color="error"
        variant="subtle"
        icon="i-hugeicons-alert-02"
        :title="error"
      />

      <div class="grid grid-cols-2 gap-2 lg:grid-cols-4">
        <div
          v-for="stat in readStats"
          :key="stat.label"
          class="rounded-md border border-default bg-muted/20 px-3 py-2"
        >
          <div class="text-xs text-muted">{{ stat.label }}</div>
          <div class="text-lg font-semibold tabular-nums">
            {{ stat.value.toLocaleString() }}
          </div>
        </div>
      </div>

      <div class="grid grid-cols-1 items-start gap-3 xl:grid-cols-2">
        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Reads over time</span>
          </template>
          <MetricChart
            :option="readsOption"
            :loading="loadingReads"
            :empty="readTypeSeries.length === 0"
            height="20rem"
          />
        </UCard>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Most-read paths</span>
          </template>
          <MetricChart
            :option="leaderboardOption"
            :loading="loadingLeaderboard"
            :empty="leaderboard.length === 0"
            height="20rem"
          />
        </UCard>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Most-read sections</span>
          </template>
          <MetricChart
            :option="sectionLeaderboardOption"
            :loading="loadingSectionLeaderboard"
            :empty="sectionLeaderboard.length === 0"
            height="20rem"
          />
        </UCard>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Most-read snippets</span>
          </template>
          <MetricChart
            :option="snippetLeaderboardOption"
            :loading="loadingSnippetLeaderboard"
            :empty="snippetLeaderboard.length === 0"
            height="20rem"
          />
        </UCard>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { MetricLeaderboardItem, MetricSeriesPoint } from "@/api/generated";
import { toMetricNumber } from "@/stores/metrics-store";
import type {
  MetricChartDomain,
  MetricDateRange,
  MetricPeriod,
} from "@/stores/metric-period";
import MetricChart from "@/views/home/MetricChart.vue";
import MetricPeriodNotice from "@/views/home/MetricPeriodNotice.vue";
import MetricsWindowSelect from "@/views/home/MetricsWindowSelect.vue";
import {
  leaderboardBarOption,
  pivotByBucket,
  timeSeriesOption,
} from "@/views/home/chart-options";

const props = defineProps<{
  /** Applied reporting period shared with the Home dashboard. */
  period: MetricPeriod;
  /** Resolved chart domain (explicit bounds, bucket width, UTC mode). */
  domain: MetricChartDomain;
  recentRanges: MetricDateRange[];
  /** UTC "today" (yyyy-MM-dd) for the calendar and live/historical classification. */
  todayUtc: string;
  documentReadSeries: MetricSeriesPoint[];
  sectionReadSeries: MetricSeriesPoint[];
  snippetReadSeries: MetricSeriesPoint[];
  leaderboard: MetricLeaderboardItem[];
  sectionLeaderboard: MetricLeaderboardItem[];
  snippetLeaderboard: MetricLeaderboardItem[];
  loadingReads: boolean;
  loadingLeaderboard: boolean;
  loadingSectionLeaderboard: boolean;
  loadingSnippetLeaderboard: boolean;
  refreshing: boolean;
  error: string | null;
}>();

const emits = defineEmits<{
  refresh: [];
  selectPeriod: [value: MetricPeriod];
}>();

/** Library-scoped read counters are projected into read-type series for the trend chart. */
const readTypeSeries = computed<MetricSeriesPoint[]>(() => [
  ...withSeriesLabel(props.documentReadSeries, "Documents"),
  ...withSeriesLabel(props.sectionReadSeries, "Sections"),
  ...withSeriesLabel(props.snippetReadSeries, "Snippets"),
]);

const readStats = computed(() => {
  const documents = sumSeries(props.documentReadSeries);
  const sections = sumSeries(props.sectionReadSeries);
  const snippets = sumSeries(props.snippetReadSeries);
  return [
    { label: "Total reads", value: documents + sections + snippets },
    { label: "Documents", value: documents },
    { label: "Sections", value: sections },
    { label: "Snippets", value: snippets },
  ];
});

const readsOption = computed(() =>
  timeSeriesOption(
    pivotByBucket(
      readTypeSeries.value,
      (point) => point.bucket,
      (point) => point.seriesKey,
      (point) => toMetricNumber(point.value),
      props.domain,
    ),
    { maxSeries: 3 },
  ),
);

const leaderboardOption = computed(() =>
  leaderboardBarOption(props.leaderboard),
);
const sectionLeaderboardOption = computed(() =>
  leaderboardBarOption(props.sectionLeaderboard),
);
const snippetLeaderboardOption = computed(() =>
  leaderboardBarOption(props.snippetLeaderboard),
);

function withSeriesLabel(
  points: MetricSeriesPoint[],
  seriesKey: string,
): MetricSeriesPoint[] {
  return points.map((point) => ({ ...point, seriesKey }));
}

function sumSeries(points: MetricSeriesPoint[]): number {
  return points.reduce(
    (total, point) => total + toMetricNumber(point.value),
    0,
  );
}
</script>
