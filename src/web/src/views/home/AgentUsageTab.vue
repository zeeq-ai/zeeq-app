<template>
  <!--
  Agent usage tab: the first practical cost/usage panels from v2 telemetry.
  All three use summed series so the charts stay cheap to query and easy to
  interpret at dashboard scale.
  -->
  <div class="flex flex-col gap-4">
    <div class="grid grid-cols-1 items-start gap-4 xl:grid-cols-2">
      <UCard :ui="{ header: 'py-3.5 sm:py-3.5', body: 'p-0 sm:p-0' }">
        <template #header>
          <div class="flex items-center justify-between gap-3">
            <span class="min-w-0 truncate font-medium">
              Token usage by model (aggregate)
            </span>
            <UTabs
              v-model="tokenModelMode"
              :items="tokenModelModeItems"
              :content="false"
              color="neutral"
              variant="pill"
              size="xs"
              class="shrink-0"
              :ui="{
                list: 'h-7 w-auto p-0.5',
                trigger: 'h-6 grow-0 px-2 py-0 text-xs',
              }"
            />
          </div>
        </template>
        <MetricChart
          :option="tokenByModelOption"
          :loading="loadingActiveTokenByModel"
          :empty="activeTokenByModelSeriesLength === 0"
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
          <span class="font-medium">Aggregate cost by user over time</span>
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
import type {
  MetricSeriesPoint,
  MetricTwoDimensionalSeriesPoint,
} from "@/api/generated";
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
  agentTokenByModelUserSeries: MetricTwoDimensionalSeriesPoint[];
  agentCostUsdSeries: MetricSeriesPoint[];
  loadingTokenByModel: boolean;
  loadingTokenByModelUser: boolean;
  loadingTokenByUser: boolean;
  loadingCostUsd: boolean;
  window: MetricWindowToken;
}>();

const tokenModelMode = ref<"total" | "byUser">("total");
const tokenModelModeItems = [
  { label: "Total", value: "total" },
  { label: "By User", value: "byUser" },
];

// NOTE: Empty/loading state is intentionally mode-local: if the user selects "By User" and the
// two-dimensional series has no points, the chart should show empty even when the total series has data.
const activeTokenByModelSeriesLength = computed(() =>
  tokenModelMode.value === "byUser"
    ? props.agentTokenByModelUserSeries.length
    : props.agentTokenByModelSeries.length,
);
const loadingActiveTokenByModel = computed(() =>
  tokenModelMode.value === "byUser"
    ? props.loadingTokenByModelUser
    : props.loadingTokenByModel,
);

/** Token totals grouped by model, using the model tag emitted by agent telemetry. */
const tokenByModelOption = computed(() =>
  tokenModelMode.value === "byUser"
    ? timeSeriesOption(
        pivotAgentTwoDimensionalSeries(props.agentTokenByModelUserSeries),
        { maxSeries: 100 },
      )
    : timeSeriesOption(pivotAgentSeries(props.agentTokenByModelSeries)),
);

/** Token totals grouped by user_email for cost attribution and noisy-user detection. */
const tokenByUserOption = computed(() =>
  timeSeriesOption(
    pivotAgentSeries(props.agentTokenByUserSeries, emailLocalPartLabel),
  ),
);

/** USD spend per bucket grouped by user identity. */
const costUsdOption = computed(() =>
  timeSeriesOption(
    pivotAgentSeries(props.agentCostUsdSeries, emailIdentityLabel),
    // NOTE: The dashboard intentionally allows up to 100 user cost series for this telemetry view.
    { maxSeries: 100 },
  ),
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

/** Converts two-dimensional API points into model/user series labels. */
function pivotAgentTwoDimensionalSeries(
  points: MetricTwoDimensionalSeriesPoint[],
) {
  return pivotByBucket(
    points,
    (point) => point.bucket,
    (point) =>
      modelUserLabel(point.primarySeriesKey ?? null, point.secondarySeriesKey),
    (point) => toMetricNumber(point.value),
    metricWindowRangeMs(props.window),
  );
}

/** Keeps the primary model dimension visible while anonymizing the user identity. */
function modelUserLabel(
  model: string | null,
  userEmail: string | null,
): string | null {
  const modelLabel = model ?? "Unknown model";
  const userLabel = emailIdentityLabel(userEmail) ?? "Unknown user";
  // NOTE: Keep the model first because this chart lives under the model usage card; the user hash
  // preserves full-email uniqueness when local parts collide across domains.
  return `${modelLabel} / ${userLabel}`;
}

/** Default series label passthrough for non-email dimensions. */
function identityLabel(value: string | null): string | null {
  return value;
}

/** Keeps user legends readable without collapsing same local-part identities. */
function emailLocalPartLabel(value: string | null): string | null {
  return value ? emailLocalPart(value) : null;
}

/** Displays an email as local-part plus a short hash of the full identity. */
function emailIdentityLabel(value: string | null): string | null {
  if (!value) {
    return null;
  }

  return `${emailLocalPart(value)} (${shortStableHash(value)})`;
}

/** Keeps a non-empty email local-part when the backend grouped by full email. */
function emailLocalPart(value: string): string {
  return value.split("@", 1)[0] || value;
}

/** Deterministic non-cryptographic 24-bit label hash for chart legends. */
function shortStableHash(value: string): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < value.length; index += 1) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }

  return (hash >>> 8).toString(16).padStart(6, "0").slice(0, 6);
}
</script>
