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
              Token usage by model
              <span class="font-light text-dimmed">
                (aggregate; all tokens)
              </span>
            </span>
            <UTabs
              v-model="tokenModelMode"
              :items="tokenModelModeItems"
              :content="false"
              color="neutral"
              variant="pill"
              size="xs"
              class="shrink-0"
              :ui="compactTabsUi"
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
          <div class="flex items-center justify-between gap-3">
            <span class="min-w-0 truncate font-medium"
              >Token usage by user
              <span class="font-light text-dimmed">
                (aggregate; all tokens, API rates)
              </span>
            </span>
            <div class="flex shrink-0 items-center gap-1">
              <UPopover
                mode="hover"
                enable-touch
                :open-delay="300"
                :close-delay="150"
                :content="{ side: 'bottom', align: 'end', sideOffset: 8 }"
                :ui="{ content: 'w-96 max-w-[calc(100vw-2rem)]' }"
              >
                <UButton
                  icon="i-hugeicons-information-circle"
                  aria-label="Telemetry alias matching"
                  color="neutral"
                  variant="ghost"
                  size="xs"
                  square
                />

                <template #content>
                  <UAlert
                    title="Email alias matching"
                    description="Member usage matches telemetry owner emails. Add an email alias only if your model provider account uses a different email than your Zeeq sign-in identity."
                    icon="i-hugeicons-user-id-verification"
                    color="neutral"
                    variant="soft"
                    orientation="horizontal"
                    :actions="aliasAlertActions"
                    :ui="aliasAlertUi"
                  />
                </template>
              </UPopover>

              <UTabs
                v-model="tokenUserPanel"
                :items="tokenUserPanelItems"
                :content="false"
                color="neutral"
                variant="pill"
                size="xs"
                class="shrink-0"
                :ui="compactTabsUi"
              />
            </div>
          </div>
        </template>
        <MetricChart
          v-if="tokenUserPanel === 'chart'"
          :option="tokenByUserOption"
          :loading="loadingTokenByUser"
          :empty="agentTokenByUserSeries.length === 0"
        />
        <div v-else class="h-96">
          <UListbox
            v-model="selectedMemberEmail"
            value-key="value"
            :items="memberUsageItems"
            :loading="loadingMembers"
            :filter="{
              placeholder: 'Filter members...',
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
              <span v-if="!item.hasData" class="text-xs text-muted">
                (no data)
              </span>
              <div v-else class="flex items-center gap-3">
                <span class="w-24 text-right font-mono text-xs text-muted">
                  {{ formatCompactTokens(item.totalTokens) }}
                </span>
                <span class="w-16 text-right font-mono text-xs text-muted">
                  {{ formatUsd(item.totalCostUsd) }}
                </span>
                <UProgress
                  :model-value="item.progressValue"
                  :max="100"
                  inverted
                  color="neutral"
                  size="sm"
                  class="w-48"
                  :get-value-label="
                    () => `${formatWholeNumber(item.totalTokens)} tokens`
                  "
                  :get-value-text="
                    () => `${formatWholeNumber(item.totalTokens)} tokens`
                  "
                />
              </div>
            </template>
          </UListbox>
        </div>
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }" class="xl:col-span-2">
        <template #header>
          <span class="font-medium">Aggregate cost by user over time</span>
          <span class="font-light text-xm text-muted">
            (approximated based on reported token usage and API pricing)</span
          >
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
import type { ListboxItem, TabsItem } from "@nuxt/ui";
import type {
  MemberResponse,
  MetricSeriesPoint,
  MetricTwoDimensionalSeriesPoint,
} from "@/api/generated";
import {
  formatMetricMillions,
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
  members: MemberResponse[];
  loadingTokenByModel: boolean;
  loadingTokenByModelUser: boolean;
  loadingTokenByUser: boolean;
  loadingCostUsd: boolean;
  loadingMembers: boolean;
  window: MetricWindowToken;
}>();

type TokenUserPanel = "chart" | "members";

type MemberUsageItem = ListboxItem & {
  value: string;
  hasData: boolean;
  totalTokens: number;
  totalCostUsd: number;
  progressValue: number;
};

const tokenUserPanel = ref<TokenUserPanel>("members");
const tokenModelMode = ref<"total" | "byUser">("byUser");
const selectedMemberEmail = ref<string | undefined>();

const tokenUserPanelItems: TabsItem[] = [
  { label: "Members", value: "members" },
  { label: "Chart", value: "chart" },
];
const tokenModelModeItems: TabsItem[] = [
  { label: "By User", value: "byUser" },
  { label: "Total", value: "total" },
];
const compactTabsUi = {
  list: "h-7 w-auto p-0.5",
  trigger: "h-6 grow-0 px-2 py-0 text-xs",
};
const aliasAlertActions = [
  {
    label: "Set alias",
    icon: "i-hugeicons-arrow-right-02",
    color: "neutral" as const,
    variant: "ghost" as const,
    to: "/settings/me",
  },
];
const aliasAlertUi = {
  root: "rounded-md",
  title: "text-sm",
  description: "text-xs",
};

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

const tokenTotalsByEmail = computed(() => {
  const totals = new Map<string, number>();
  for (const point of props.agentTokenByUserSeries) {
    const email = point.seriesKey;
    if (!email) {
      continue;
    }

    totals.set(email, (totals.get(email) ?? 0) + toMetricNumber(point.value));
  }

  return totals;
});

const costTotalsByEmail = computed(() => {
  const totals = new Map<string, number>();
  for (const point of props.agentCostUsdSeries) {
    const email = point.seriesKey;
    if (!email) {
      continue;
    }

    totals.set(email, (totals.get(email) ?? 0) + toMetricNumber(point.value));
  }

  return totals;
});

const memberUsageItems = computed<MemberUsageItem[]>(() => {
  const totals = tokenTotalsByEmail.value;
  const costs = costTotalsByEmail.value;
  const maxTokens = Math.max(0, ...Array.from(totals.values()));

  return props.members
    .map((member) => {
      const usageKey = member.email ?? member.userId;
      const totalTokens = totals.get(usageKey) ?? 0;
      const totalCostUsd = costs.get(usageKey) ?? 0;
      const hasData = totalTokens > 0;

      return {
        label: member.displayName || member.email || member.userId,
        value: usageKey,
        avatar: {
          src: member.pictureUrl || undefined,
          alt: member.displayName || member.email || member.userId,
        },
        hasData,
        totalTokens,
        totalCostUsd,
        progressValue:
          hasData && maxTokens > 0 ? (totalTokens / maxTokens) * 100 : 0,
      };
    })
    .sort((left, right) => {
      if (right.totalTokens !== left.totalTokens) {
        return right.totalTokens - left.totalTokens;
      }

      return (left.label ?? "").localeCompare(right.label ?? "");
    });
});

/** Token charts use millions on the value axis so 1M+ usage remains scannable. */
const tokenMillionAxisOptions = {
  yAxisName: "Tokens (M)",
  yAxisLabelFormatter: formatMetricMillions,
};

/** Token totals grouped by model, using the model tag emitted by agent telemetry. */
const tokenByModelOption = computed(() =>
  tokenModelMode.value === "byUser"
    ? timeSeriesOption(
        pivotAgentTwoDimensionalSeries(props.agentTokenByModelUserSeries),
        { maxSeries: 100, ...tokenMillionAxisOptions },
      )
    : timeSeriesOption(pivotAgentSeries(props.agentTokenByModelSeries), {
        ...tokenMillionAxisOptions,
      }),
);

/** Token totals grouped by user_email for cost attribution and noisy-user detection. */
const tokenByUserOption = computed(() =>
  timeSeriesOption(
    pivotAgentSeries(props.agentTokenByUserSeries, emailLocalPartLabel),
    tokenMillionAxisOptions,
  ),
);

/** USD spend per bucket grouped by user identity. */
const costUsdOption = computed(() =>
  timeSeriesOption(
    pivotAgentSeries(props.agentCostUsdSeries, emailIdentityLabel),
    // NOTE: The dashboard intentionally allows up to 100 user cost series for this telemetry view.
    { maxSeries: 100, yAxisName: "USD", yAxisLabelFormatter: formatUsd },
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
  return `${modelLabel}\n${userLabel}`;
}

/** Default series label passthrough for non-email dimensions. */
function identityLabel(value: string | null): string | null {
  return value;
}

/** Keeps user legends readable without collapsing same local-part identities. */
function emailLocalPartLabel(value: string | null): string | null {
  return value ? emailLocalPart(value) : null;
}

/**
 * Displays an email as local-part plus a short hash of the full identity, on
 * its own line — the combined form overflows the 140px legend column,
 * especially stacked under the model name in the by-user breakdown.
 */
function emailIdentityLabel(value: string | null): string | null {
  if (!value) {
    return null;
  }

  return `${emailLocalPart(value)}\n(${shortStableHash(value)})`;
}

/** Keeps a non-empty email local-part when the backend grouped by full email. */
function emailLocalPart(value: string): string {
  return value.split("@", 1)[0] || value;
}

function formatWholeNumber(value: number): string {
  return Math.round(value).toLocaleString();
}

function formatCompactTokens(value: number): string {
  return `${formatMetricMillions(value)}M`;
}

function formatUsd(value: number): string {
  return value.toLocaleString(undefined, {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: 2,
  });
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
