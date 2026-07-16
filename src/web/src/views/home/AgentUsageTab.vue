<template>
  <!--
  Agent usage tab: the first practical cost/usage panels from v2 telemetry.
  All three use summed series so the charts stay cheap to query and easy to
  interpret at dashboard scale.
  -->
  <div class="flex flex-col gap-4">
    <div class="grid grid-cols-1 items-start gap-4 xl:grid-cols-2">
      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Token usage by model (aggregate)</span>
        </template>
        <MetricChart
          :option="tokenByModelOption"
          :loading="loadingTokenByModel"
          :empty="agentTokenByModelSeries.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Token usage by user (aggregate)</span>
        </template>
        <MetricChart
          :option="tokenByUserOption"
          :loading="loadingTokenByUser"
          :empty="agentTokenByUserSeries.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }" class="xl:col-span-2">
        <template #header>
          <span class="font-medium">Aggregate cost over time</span>
        </template>
        <MetricChart
          :option="costUsdOption"
          :loading="loadingCostUsd"
          :empty="agentCostUsdSeries.length === 0"
        />
      </UCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { MetricSeriesPoint } from "@/api/generated";
import {
  metricWindowRangeMs,
  toMetricNumber,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import MetricChart from "./MetricChart.vue";
import { pivotByBucket, timeSeriesOption } from "./chart-options";

const props = defineProps<{
  agentTokenByModelSeries: MetricSeriesPoint[];
  agentTokenByUserSeries: MetricSeriesPoint[];
  agentCostUsdSeries: MetricSeriesPoint[];
  loadingTokenByModel: boolean;
  loadingTokenByUser: boolean;
  loadingCostUsd: boolean;
  window: MetricWindowToken;
}>();

/** Token totals grouped by model, using the model tag emitted by agent telemetry. */
const tokenByModelOption = computed(() =>
  timeSeriesOption(pivotAgentSeries(props.agentTokenByModelSeries)),
);

/** Token totals grouped by user_email for cost attribution and noisy-user detection. */
const tokenByUserOption = computed(() =>
  timeSeriesOption(
    pivotAgentSeries(props.agentTokenByUserSeries, emailLocalPartLabel),
  ),
);

/** Organization-wide USD spend per bucket; series key is intentionally ungrouped. */
const costUsdOption = computed(() =>
  timeSeriesOption(pivotAgentSeries(props.agentCostUsdSeries, totalLabel), {
    maxSeries: 1,
    showLegend: false,
  }),
);

/** Converts API series points into the dashboard's shared fixed-bucket chart shape. */
function pivotAgentSeries(
  points: MetricSeriesPoint[],
  seriesKeyLabel: (seriesKey: string | null) => string | null = identityLabel,
) {
  return pivotByBucket(
    points,
    (point) => point.bucket,
    (point) => seriesKeyLabel(point.seriesKey ?? null),
    (point) => toMetricNumber(point.value),
    metricWindowRangeMs(props.window),
  );
}

/** Default series label passthrough for non-email dimensions. */
function identityLabel(value: string | null): string | null {
  return value;
}

/** Stable label for intentionally ungrouped aggregate series. */
function totalLabel(value: string | null): string {
  return value ?? "Total";
}

/** Keeps user legends readable by displaying only the email local-part. */
function emailLocalPartLabel(value: string | null): string | null {
  return value ? emailLocalPart(value) : null;
}

/** Keeps a non-empty email local-part when the backend grouped by full email. */
function emailLocalPart(value: string): string {
  return value.split("@", 1)[0] || value;
}
</script>
