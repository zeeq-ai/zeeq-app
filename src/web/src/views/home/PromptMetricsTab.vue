<template>
  <!--
  Skills tab: successful dynamic MCP prompt reads. These are first-party server
  metrics emitted when a scoped skill document is retrieved through prompts/get,
  not raw agent telemetry.
  -->
  <div class="flex flex-col gap-4">
    <!-- User + library filters scope all prompt panels. -->
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
      <USelect
        :model-value="library"
        :items="libraryItems"
        icon="i-hugeicons-library"
        class="w-56"
        @update:model-value="(value) => emits('update:library', value)"
      />
    </div>

    <div class="grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Skill reads by user</span>
        </template>
        <MetricChart
          :option="byUserOption"
          :loading="loadingByUser"
          :empty="promptGetByUserSeries.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Skill reads by library</span>
        </template>
        <MetricChart
          :option="byLibraryOption"
          :loading="loadingByLibrary"
          :empty="promptGetByLibrarySeries.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Top skills by reads</span>
        </template>
        <MetricChart
          :option="leaderboardOption"
          :loading="loadingLeaderboard"
          :empty="promptLeaderboard.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Skill reads by client</span>
        </template>
        <MetricChart
          :option="byClientOption"
          :loading="loadingByClient"
          :empty="promptGetByUserAgentSeries.length === 0"
          legend-size="md"
        />
      </UCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useColorMode } from "@vueuse/core";
import type { MetricLeaderboardItem, MetricSeriesPoint } from "@/api/generated";
import {
  metricWindowRangeMs,
  toMetricNumber,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import MetricChart from "./MetricChart.vue";
import {
  leaderboardBarOption,
  metricDonutOption,
  pivotByBucket,
  timeSeriesOption,
  truncateAgentLabel,
} from "./chart-options";

const props = defineProps<{
  promptGetByUserSeries: MetricSeriesPoint[];
  promptGetByLibrarySeries: MetricSeriesPoint[];
  promptGetByUserAgentSeries: MetricSeriesPoint[];
  promptLeaderboard: MetricLeaderboardItem[];
  loadingByUser: boolean;
  loadingByLibrary: boolean;
  loadingByClient: boolean;
  loadingLeaderboard: boolean;
  /** Selected user emails (empty = all). */
  users: string[];
  /** User filter options ({ label, value: email }). */
  userItems: { label: string; value: string }[];
  /** Current library filter value (name, or the root's "all" sentinel). */
  library: string;
  /** Library options ({ label, value }) including the leading "All" entry. */
  libraryItems: { label: string; value: string }[];
  /** Shared dashboard window; fills empty buckets so the x-axis reflects the true cadence. */
  window: MetricWindowToken;
}>();

const emits = defineEmits<{
  "update:users": [value: string[]];
  "update:library": [value: string];
}>();

const colorMode = useColorMode();
const isDark = computed(() => colorMode.value === "dark");

/** Stacked prompt-get volume by user over time. */
const byUserOption = computed(() =>
  timeSeriesOption(pivot(props.promptGetByUserSeries, emailLocalPart), {
    maxSeries: 50,
  }),
);

/** Aggregate prompt-get mix by library. */
const byLibraryOption = computed(() =>
  metricDonutOption(props.promptGetByLibrarySeries, isDark.value, 20),
);

/** Ranked horizontal bar of the most retrieved skills. */
const leaderboardOption = computed(() =>
  leaderboardBarOption(props.promptLeaderboard),
);

/** Stacked prompt-get volume by MCP client/user-agent over time. */
const byClientOption = computed(() =>
  timeSeriesOption(pivot(props.promptGetByUserAgentSeries), {
    seriesLabel: truncateAgentLabel,
  }),
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
</script>
