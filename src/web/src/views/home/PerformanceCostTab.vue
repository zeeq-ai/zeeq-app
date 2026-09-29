<template>
  <div class="flex flex-col gap-4">
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

      <UCard :ui="{ header: 'py-3.5 sm:py-3.5', body: 'p-0 sm:p-0' }">
        <template #header>
          <div class="flex items-center justify-between gap-3">
            <span class="font-medium">Cost per review</span>
            <UTabs
              v-model="costMode"
              :items="costModeItems"
              :content="false"
              color="neutral"
              variant="pill"
              size="xs"
              :ui="compactTabsUi"
            />
          </div>
        </template>
        <MetricChart
          :option="costOption"
          :loading="loadingCosts"
          :empty="
            costMode === 'byReview'
              ? reviewCostMetrics.reviews.length === 0
              : reviewCostMetrics.byAuthor.length === 0
          "
          @point-click="openReviewPoint"
        />
        <p
          v-if="costMode === 'byReview' && reviewCostMetrics.hasMoreReviews"
          class="border-t border-default px-4 py-2 text-xs text-muted"
        >
          Showing the latest 2,000 reviews. Aggregate and author totals include
          all reviews in this period.
        </p>
      </UCard>

      <UCard :ui="{ header: 'py-3.5 sm:py-3.5', body: 'p-0 sm:p-0' }">
        <template #header>
          <div class="flex items-center justify-between gap-3">
            <span class="font-medium">Review cost by author</span>
            <UTabs
              v-model="authorMode"
              :items="authorModeItems"
              :content="false"
              color="neutral"
              variant="pill"
              size="xs"
              :ui="compactTabsUi"
            />
          </div>
        </template>
        <MetricChart
          v-if="authorMode === 'chart'"
          :option="authorOption"
          :loading="loadingCosts"
          :empty="reviewCostMetrics.byAuthor.length === 0"
          legend-size="lg"
        />
        <div v-else class="h-96">
          <UListbox
            :items="authorItems"
            :loading="loadingCosts"
            value-key="value"
            :filter="{
              placeholder: 'Filter authors...',
              icon: 'i-hugeicons-search-01',
            }"
            :filter-fields="['label']"
            class="size-full"
            :ui="{
              root: 'ring-0 rounded-none',
              input: 'border-b border-default px-4',
              content: 'max-h-none',
              group: 'p-0',
              item: 'px-4 py-1.5',
            }"
          >
            <template #item-trailing="{ item }">
              <div class="flex items-center gap-3">
                <span class="w-24 text-right font-mono text-xs text-muted">
                  {{ formatUsd(item.totalCostUsd) }}
                </span>
                <UProgress
                  :model-value="item.progressValue"
                  :max="100"
                  inverted
                  color="neutral"
                  size="sm"
                  class="w-40"
                  :get-value-label="() => formatUsd(item.totalCostUsd)"
                  :get-value-text="() => formatUsd(item.totalCostUsd)"
                />
              </div>
            </template>
          </UListbox>
        </div>
      </UCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ListboxItem, TabsItem } from "@nuxt/ui";
import type {
  MetricPercentilePoint,
  MetricScatterPoint,
  ReviewCostMetrics,
} from "@/api/generated";
import {
  metricWindowRangeMs,
  toMetricNumber,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import MetricChart from "./MetricChart.vue";
import {
  percentileLinesOption,
  pivotByBucket,
  reviewCostScatterOption,
  timeSeriesOption,
  tokenScatterOption,
} from "./chart-options";

const props = defineProps<{
  durationPercentiles: MetricPercentilePoint[];
  durationScatter: MetricScatterPoint[];
  reviewCostMetrics: ReviewCostMetrics;
  loadingPercentiles: boolean;
  loadingScatter: boolean;
  loadingCosts: boolean;
  window: MetricWindowToken;
}>();

const router = useRouter();
const costMode = ref<"byReview" | "aggregate">("byReview");
const authorMode = ref<"members" | "chart">("members");
const costModeItems: TabsItem[] = [
  { label: "By Review", value: "byReview" },
  { label: "Aggregate", value: "aggregate" },
];
const authorModeItems: TabsItem[] = [
  { label: "Members", value: "members" },
  { label: "Chart", value: "chart" },
];
const compactTabsUi = {
  list: "h-7 w-auto p-0.5",
  trigger: "h-6 grow-0 px-2 py-0 text-xs",
};

const percentileOption = computed(() =>
  percentileLinesOption(props.durationPercentiles),
);
const scatterOption = computed(() => tokenScatterOption(props.durationScatter));
const scatterWithTokens = computed(
  () => props.durationScatter.filter((point) => point.tokens !== null).length,
);

/** Bucketed sums include every event in the window, even when the raw chart is capped. */
const aggregateOption = computed(() =>
  timeSeriesOption(
    pivotByBucket(
      props.reviewCostMetrics.byAuthor,
      (point) => point.bucket,
      () => null,
      (point) => toMetricNumber(point.value),
      metricWindowRangeMs(props.window),
    ),
    { showLegend: false, yAxisName: "USD", yAxisLabelFormatter: formatUsd },
  ),
);
const costOption = computed(() =>
  costMode.value === "byReview"
    ? reviewCostScatterOption(props.reviewCostMetrics.reviews)
    : aggregateOption.value,
);
const authorOption = computed(() =>
  timeSeriesOption(
    pivotByBucket(
      props.reviewCostMetrics.byAuthor,
      (point) => point.bucket,
      (point) => point.seriesKey ?? "Unknown",
      (point) => toMetricNumber(point.value),
      metricWindowRangeMs(props.window),
    ),
    { maxSeries: 100, yAxisName: "USD", yAxisLabelFormatter: formatUsd },
  ),
);

type AuthorItem = ListboxItem & {
  value: string;
  totalCostUsd: number;
  progressValue: number;
};

/** Ranked totals use PR author logins and authenticated MCP caller emails. */
const authorItems = computed<AuthorItem[]>(() => {
  const totals = new Map<string, number>();
  for (const point of props.reviewCostMetrics.byAuthor) {
    const author = point.seriesKey ?? "Unknown";
    totals.set(author, (totals.get(author) ?? 0) + toMetricNumber(point.value));
  }
  const maxCost = Math.max(0, ...totals.values());
  return [...totals.entries()]
    .map(([author, cost]) => ({
      label: author,
      value: author,
      totalCostUsd: cost,
      progressValue: maxCost > 0 ? (cost / maxCost) * 100 : 0,
    }))
    .sort((left, right) => right.totalCostUsd - left.totalCostUsd);
});

/** Opens the exact partition-aware review link carried by the metric event. */
function openReviewPoint(event: unknown) {
  if (
    costMode.value !== "byReview" ||
    typeof event !== "object" ||
    event === null ||
    !("dataIndex" in event) ||
    typeof event.dataIndex !== "number"
  ) {
    return;
  }

  const review = props.reviewCostMetrics.reviews[event.dataIndex];
  if (review) {
    router.push({
      name: "CodeReviewSingle",
      params: { reviewId: review.reviewId },
      query: { c: review.viewToken },
    });
  }
}

function formatUsd(value: number): string {
  return value.toLocaleString(undefined, {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: 4,
  });
}
</script>
