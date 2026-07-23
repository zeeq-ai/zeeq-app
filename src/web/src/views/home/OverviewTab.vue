<template>
  <!--
  Overview tab: headline stat cards (UI-0) plus four tool-call panels — by
  user, by agent, by tool name (aggregate donut), and by tool name over time
  (trend). All but the donut are grouped time series pivoted onto a shared
  bucket axis by the chart-options builders (by user/by agent were relocated
  here from the former MCP Usage tab, which had no other content once they
  moved).
  -->
  <div class="flex flex-col gap-4">
    <UEmpty
      v-if="!loading && !overview"
      icon="i-hugeicons-chart-01"
      title="No activity yet"
      description="Metrics appear here once tools, reads, and reviews are recorded for this window."
    />

    <template v-else>
      <div class="grid grid-cols-2 gap-4 md:grid-cols-3">
        <!--
        One card per headline number; values fall back to em dash while loading.
        Critical/Major cards get a "View" button on their header row (floated
        right) that opens the findings drill-down slideover, scoped to that
        severity — an explicit affordance rather than a whole-card click,
        which read as non-interactive.
        -->
        <UCard v-for="card in cards" :key="card.label">
          <div class="flex items-center justify-between gap-2">
            <div class="flex items-center gap-2 text-sm text-muted">
              <UIcon :name="card.icon" class="size-4" />
              {{ card.label }}
            </div>
            <UButton
              v-if="card.severity"
              label="View"
              icon="i-hugeicons-arrow-right-01"
              trailing
              color="neutral"
              variant="ghost"
              size="xs"
              @click="openFindingReviews(card.severity)"
            />
          </div>
          <div class="mt-1 text-2xl font-semibold tabular-nums">
            {{ card.value }}
          </div>
        </UCard>
      </div>

      <!--
      Findings drill-down slideover — a single instance opened by either the
      Critical or Major card's "View" button; it switches severities via its
      own tabs rather than needing a separate instance per card.
      -->
      <FindingReviewsSlideover
        v-model:open="slideoverOpen"
        :initial-severity="initialSeverity"
        :parent-window="window"
        :items="findingReviewItems"
        :next-cursor="findingReviewNextCursor"
        :loading-by-severity="findingReviewsLoading"
        :loading-more-by-severity="findingReviewsLoadingMore"
        @load="(severity, windowToken) => emits('loadFindingReviews', severity, windowToken)"
        @load-more="(severity, windowToken) => emits('loadMoreFindingReviews', severity, windowToken)"
      />

      <!-- User + tool multi-select filters scope all four tool-call panels below. -->
      <div class="flex flex-wrap justify-end gap-3">
        <USelectMenu
          :model-value="users"
          :items="userItems"
          value-key="value"
          multiple
          icon="i-hugeicons-user"
          placeholder="All users"
          class="w-56"
          @update:model-value="(value) => emits('update:users', value)"
        />
        <USelectMenu
          :model-value="tools"
          :items="toolItems"
          value-key="value"
          multiple
          icon="i-hugeicons-wrench-01"
          placeholder="All tools"
          class="w-56"
          @update:model-value="(value) => emits('update:tools', value)"
        />
      </div>

      <!-- 2x2 grid; stacks to one column on narrow viewports. -->
      <div class="grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Tool calls by user</span>
          </template>
          <MetricChart
            :option="toolCallOption"
            :loading="loadingToolCalls"
            :empty="toolCallSeries.length === 0"
            height="18rem"
          />
        </UCard>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Tool calls by agent</span>
          </template>
          <MetricChart
            :option="userAgentOption"
            :loading="loadingUserAgents"
            :empty="userAgentSeries.length === 0"
            height="18rem"
          />
        </UCard>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Tool calls by tool name</span>
          </template>
          <MetricChart
            :option="toolCallByToolOption"
            :loading="loadingToolCallByTool"
            :empty="toolCallByToolSeries.length === 0"
            height="18rem"
          />
        </UCard>

        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header>
            <span class="font-medium">Tool call volume by tool</span>
          </template>
          <MetricChart
            :option="toolCallByToolTrendOption"
            :loading="loadingToolCallByTool"
            :empty="toolCallByToolSeries.length === 0"
            height="18rem"
          />
        </UCard>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { useColorMode } from "@vueuse/core";
import {
  findingSeverityEnum,
  type FindingReviewListItemResponse,
  type FindingSeverity,
  type MetricsOverview,
  type MetricSeriesPoint,
} from "@/api/generated";
import {
  metricWindowRangeMs,
  toMetricNumber,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import MetricChart from "./MetricChart.vue";
import FindingReviewsSlideover from "./FindingReviewsSlideover.vue";
import {
  pivotByBucket,
  timeSeriesOption,
  toolCallDonutOption,
} from "./chart-options";

const colorMode = useColorMode();
const isDark = computed(() => colorMode.value === "dark");

const props = defineProps<{
  overview: MetricsOverview | null;
  loading: boolean;
  toolCallSeries: MetricSeriesPoint[];
  userAgentSeries: MetricSeriesPoint[];
  toolCallByToolSeries: MetricSeriesPoint[];
  loadingToolCalls: boolean;
  loadingUserAgents: boolean;
  loadingToolCallByTool: boolean;
  /** Selected user emails (empty = all). */
  users: string[];
  /** User filter options ({ label, value: email }). */
  userItems: { label: string; value: string }[];
  /** Selected tool names (empty = all). */
  tools: string[];
  /** Tool filter options ({ label, value: tool }). */
  toolItems: { label: string; value: string }[];
  /** Shared dashboard window; fills empty buckets so the x-axis reflects the true cadence. */
  window: MetricWindowToken;
  /** Findings drill-down pages, keyed by severity — see the metrics store action's comment. */
  findingReviewItems: Record<FindingSeverity, FindingReviewListItemResponse[]>;
  /** Next-page cursor per severity, or null when that severity's list is exhausted. */
  findingReviewNextCursor: Record<FindingSeverity, string | null>;
  /** True while a severity's first page is loading. */
  findingReviewsLoading: Record<FindingSeverity, boolean>;
  /** True while a severity's next page (via "Load more") is loading. */
  findingReviewsLoadingMore: Record<FindingSeverity, boolean>;
}>();

const emits = defineEmits<{
  "update:users": [value: string[]];
  "update:tools": [value: string[]];
  loadFindingReviews: [severity: FindingSeverity, window: MetricWindowToken];
  loadMoreFindingReviews: [
    severity: FindingSeverity,
    window: MetricWindowToken,
  ];
}>();

/** Whether the findings drill-down slideover is open. */
const slideoverOpen = ref(false);

/** Severity the slideover resets to each time it opens — set by whichever card was clicked. */
const initialSeverity = ref<FindingSeverity>(findingSeverityEnum.Critical);

/** Opens the drill-down slideover, defaulting its tab to the clicked card's severity. */
function openFindingReviews(severity: FindingSeverity) {
  initialSeverity.value = severity;
  slideoverOpen.value = true;
}

/** One headline stat card; `severity` is set only for the two clickable drill-down cards. */
type OverviewCard = {
  label: string;
  icon: string;
  value: string;
  severity?: FindingSeverity;
};

/** Card view models projected from the overview DTO (see MVVM note in guidance). */
const cards = computed<OverviewCard[]>(() => {
  const overview = props.overview;
  return [
    {
      label: "Tool calls",
      icon: "i-hugeicons-computer",
      value: formatCount(overview?.toolCalls),
    },
    {
      label: "Knowledge base reads",
      icon: "i-hugeicons-book-open-01",
      value: formatCount(overview?.knowledgeReads),
    },
    {
      label: "Reviews",
      icon: "i-hugeicons-git-pull-request",
      value: formatCount(overview?.reviews),
    },
    {
      label: "Critical findings",
      icon: "i-hugeicons-alert-02",
      value: formatCount(overview?.criticalFindings),
      severity: findingSeverityEnum.Critical,
    },
    {
      label: "Major findings",
      icon: "i-hugeicons-alert-01",
      value: formatCount(overview?.majorFindings),
      severity: findingSeverityEnum.Major,
    },
    {
      label: "p95 review time",
      icon: "i-hugeicons-clock-01",
      value: formatDuration(overview?.p95ReviewDurationMs),
    },
  ];
});

/** UI-1: stacked bar of tool calls, one band per user. */
const toolCallOption = computed(() =>
  timeSeriesOption(pivot(props.toolCallSeries, emailLocalPart), {
    maxSeries: 50,
  }),
);

/** UI-2: stacked bar of tool calls, one band per connecting agent. */
const userAgentOption = computed(() =>
  timeSeriesOption(pivot(props.userAgentSeries)),
);

/** Donut of total tool calls by tool name (aggregate mix, no time dimension). */
const toolCallByToolOption = computed(() =>
  toolCallDonutOption(props.toolCallByToolSeries, isDark.value),
);

/**
 * Stacked bar of tool calls over time, one band per tool name — the trend
 * complement to the tool-mix donut above. Reuses the same by-tool series
 * (no separate fetch).
 */
const toolCallByToolTrendOption = computed(() =>
  timeSeriesOption(pivot(props.toolCallByToolSeries)),
);

/** Pivots a counter series onto the shared bucket axis, filled across the full window. */
function pivot(
  points: MetricSeriesPoint[],
  seriesKeyLabel: (seriesKey: string) => string = identityLabel,
) {
  return pivotByBucket(
    points,
    (point) => point.bucket,
    (point) => (point.seriesKey ? seriesKeyLabel(point.seriesKey) : null),
    (point) => toMetricNumber(point.value),
    metricWindowRangeMs(props.window),
  );
}

/** Default series label passthrough for non-email dimensions. */
function identityLabel(value: string): string {
  return value;
}

/** Keeps user legends readable by displaying only the email local-part. */
function emailLocalPart(value: string): string {
  return value.split("@", 1)[0] || value;
}

/** Formats a count field, or an em dash when the overview has not loaded. */
function formatCount(value: number | string | undefined): string {
  if (value === undefined) {
    return "—";
  }
  return Math.round(toMetricNumber(value)).toLocaleString();
}

/** Formats a millisecond duration compactly (seconds once it passes 1s). */
function formatDuration(value: number | string | undefined): string {
  if (value === undefined) {
    return "—";
  }
  const ms = toMetricNumber(value);
  return ms >= 1000
    ? `${(ms / 1000).toFixed(1)} s`
    : `${Math.round(ms).toLocaleString()} ms`;
}
</script>
