<template>
  <!--
  Code Reviews tab (sourced from code_review_records, not the metric pipeline):
  finding-severity totals by repo and by origin (UI-3), review volume over time
  (UI-4), and the origin mix as a donut (UI-5).
  -->
  <div class="flex flex-col gap-4">
    <!-- Repo + author (multi) and origin (single) filters; empty/all by default. -->
    <div class="flex flex-wrap justify-end gap-3">
      <USelectMenu
        :model-value="repositoryIds"
        :items="repositoryItems"
        value-key="value"
        multiple
        icon="i-hugeicons-github"
        placeholder="All repositories"
        class="w-56"
        @update:model-value="(value) => emits('update:repositoryIds', value)"
      />
      <USelectMenu
        :model-value="authorLogins"
        :items="authorItems"
        value-key="value"
        multiple
        icon="i-hugeicons-user"
        placeholder="All authors"
        class="w-56"
        @update:model-value="(value) => emits('update:authorLogins', value)"
      />
      <USelect
        :model-value="origin"
        :items="originItems"
        icon="i-hugeicons-filter"
        class="w-56"
        @update:model-value="(value) => emits('update:origin', value)"
      />
    </div>

    <!-- Side by side on larger screens; stacked on narrow viewports. -->
    <div class="grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Findings by severity (by repository)</span>
        </template>
        <MetricChart
          :option="findingsByRepoOption"
          :loading="loadingFindings"
          :empty="reviewFindingsByRepo.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Findings by severity (by origin)</span>
        </template>
        <MetricChart
          :option="findingsByOriginOption"
          :loading="loadingFindings"
          :empty="reviewFindingsByOrigin.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Review volume</span>
        </template>
        <MetricChart
          :option="volumeOption"
          :loading="loadingVolume"
          :empty="reviewVolume.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Review mix</span>
        </template>
        <MetricChart
          :option="donutOption"
          :loading="loadingVolume"
          :empty="reviewVolume.length === 0"
        />
      </UCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useColorMode } from "@vueuse/core";
import type { ReviewFindingsPoint, ReviewVolumePoint } from "@/api/generated";
import {
  metricWindowRangeMs,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import MetricChart from "./MetricChart.vue";
import {
  severityBarOption,
  volumeBarOption,
  volumeDonutOption,
} from "./chart-options";

const colorMode = useColorMode();
const isDark = computed(() => colorMode.value === "dark");

const props = defineProps<{
  reviewFindingsByRepo: ReviewFindingsPoint[];
  reviewFindingsByOrigin: ReviewFindingsPoint[];
  reviewVolume: ReviewVolumePoint[];
  loadingFindings: boolean;
  loadingVolume: boolean;
  /** Currently selected request origin, or "" for all origins. */
  origin: string;
  /** Origin options ({ label, value }); value "" means all. */
  originItems: { label: string; value: string }[];
  /** Selected repository ids (empty = all). */
  repositoryIds: string[];
  /** Repository options ({ label: display name, value: id }), incl. soft-deleted. */
  repositoryItems: { label: string; value: string }[];
  /** Selected author logins (empty = all). */
  authorLogins: string[];
  /** Author options ({ label, value: login }). */
  authorItems: { label: string; value: string }[];
  /** Shared dashboard window; fills empty buckets so the x-axis reflects the true cadence. */
  window: MetricWindowToken;
}>();

const emits = defineEmits<{
  "update:origin": [value: string];
  "update:repositoryIds": [value: string[]];
  "update:authorLogins": [value: string[]];
}>();

/** UI-3: severity stacked bar grouped by repository. */
const findingsByRepoOption = computed(() =>
  severityBarOption(props.reviewFindingsByRepo),
);

/** UI-3: severity stacked bar grouped by request origin. */
const findingsByOriginOption = computed(() =>
  severityBarOption(props.reviewFindingsByOrigin),
);

/** UI-4: bucketed review-volume stacked bars over time, filled across the full window. */
const volumeOption = computed(() =>
  volumeBarOption(props.reviewVolume, metricWindowRangeMs(props.window)),
);

/** UI-5: donut of total review volume by series key. */
const donutOption = computed(() =>
  volumeDonutOption(props.reviewVolume, isDark.value),
);
</script>
