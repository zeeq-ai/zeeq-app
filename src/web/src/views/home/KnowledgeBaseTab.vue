<template>
  <!--
  Knowledge Base tab: total document/section/snippet reads stacked by library
  (UI-6), the most-read paths (UI-7, document-level), the most-read sections
  (path + heading, prose only), and the most-read snippets (path + heading,
  code only). Sections and snippets are kept in separate panels rather than
  combined: a prose section and a code snippet under the same heading share
  an identical (path, heading) pair (both derive HeadingPath from the same
  heading-tracking state in ZeeqDocumentParser), so a combined leaderboard
  would merge "read the explanation" and "read the code sample" into one row.
  All leaderboards are ranked horizontal bars rather than time series because
  of path/section cardinality.
  -->
  <div class="flex flex-col gap-4">
    <!-- Library filter (single-select) scopes the reads series and all three leaderboards. -->
    <div class="flex justify-end">
      <USelect
        :model-value="library"
        :items="libraryItems"
        icon="i-hugeicons-library"
        class="w-56"
        @update:model-value="(value) => emits('update:library', value)"
      />
    </div>

    <!--
    2x2 grid; stacks to one column on narrow viewports. Charts use the panel
    default height (matches the Code Reviews tab's charts) now that there
    are four panels sharing the tab instead of two.
    -->
    <div class="grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Knowledge base reads by library</span>
        </template>
        <MetricChart
          :option="readsOption"
          :loading="loadingReads"
          :empty="combinedReads.length === 0"
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
        />
      </UCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { MetricLeaderboardItem, MetricSeriesPoint } from "@/api/generated";
import {
  metricWindowRangeMs,
  toMetricNumber,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import MetricChart from "./MetricChart.vue";
import {
  leaderboardBarOption,
  pivotByBucket,
  timeSeriesOption,
} from "./chart-options";

const props = defineProps<{
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
  /** Current library filter value (name, or the root's "all" sentinel). */
  library: string;
  /** Library options ({ label, value }) including the leading "All" entry. */
  libraryItems: { label: string; value: string }[];
  /** Shared dashboard window; fills empty buckets so the x-axis reflects the true cadence. */
  window: MetricWindowToken;
}>();

const emits = defineEmits<{
  "update:library": [value: string];
}>();

/**
 * All three read counters merged into one set of points; the pivot then sums
 * document + section + snippet reads per (bucket, library) into one stacked bar.
 */
const combinedReads = computed(() => [
  ...props.documentReadSeries,
  ...props.sectionReadSeries,
  ...props.snippetReadSeries,
]);

/** UI-6: stacked bar of total reads, one band per library, filled across the full window. */
const readsOption = computed(() =>
  timeSeriesOption(
    pivotByBucket(
      combinedReads.value,
      (point) => point.bucket,
      (point) => point.seriesKey,
      (point) => toMetricNumber(point.value),
      metricWindowRangeMs(props.window),
    ),
    { maxSeries: 50 },
  ),
);

/** UI-7: ranked horizontal bar of the most-read paths (document-level). */
const leaderboardOption = computed(() =>
  leaderboardBarOption(props.leaderboard),
);

/** Ranked horizontal bar of the most-read sections (path + heading, prose only). */
const sectionLeaderboardOption = computed(() =>
  leaderboardBarOption(props.sectionLeaderboard),
);

/** Ranked horizontal bar of the most-read code snippets (path + heading, code only). */
const snippetLeaderboardOption = computed(() =>
  leaderboardBarOption(props.snippetLeaderboard),
);
</script>
