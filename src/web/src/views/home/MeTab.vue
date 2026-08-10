<template>
  <!--
  Me tab: everything scoped to the signed-in user rather than an org-wide
  or manually-filtered view. Knowledge base leaderboard toggles between
  documents/sections/snippets like the Sessions tab's Members|Chart toggle
  (AgentUsageTab.vue); recent sessions reuses the same query that backs
  MemberSessionsSlideover, just capped to 25 and no cost filter; token usage
  and tool usage reuse the exact chart builders the Overview/Sessions tabs
  already use, just fed "my" series instead of org-wide ones.
  -->
  <div class="flex flex-col gap-4">
    <div class="grid grid-cols-1 items-start gap-4 xl:grid-cols-2">
      <UCard :ui="{ header: 'py-3.5 sm:py-3.5', body: 'p-0 sm:p-0' }">
        <template #header>
          <div class="flex items-center justify-between gap-3">
            <span class="min-w-0 truncate font-medium">
              Knowledge base usage
            </span>
            <UTabs
              v-model="knowledgePanel"
              :items="knowledgePanelItems"
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
          :option="leaderboardOption"
          :loading="activeLeaderboardLoading"
          :empty="activeLeaderboardItems.length === 0"
        />
      </UCard>

      <UCard :ui="{ header: 'py-3.5 sm:py-3.5', body: 'p-0 sm:p-0' }">
        <template #header>
          <div class="flex items-center justify-between gap-3">
            <span class="min-w-0 truncate font-medium">Recent sessions</span>
            <div class="flex shrink-0 items-center gap-1">
              <UButton
                label="See details"
                icon="i-hugeicons-arrow-right-02"
                trailing
                color="neutral"
                variant="ghost"
                size="xs"
                to="/sessions"
              />
              <UButton
                label="See more"
                icon="i-hugeicons-arrow-right-02"
                trailing
                color="neutral"
                variant="soft"
                size="xs"
                @click="onSeeMoreSessions"
              />
            </div>
          </div>
        </template>

        <div v-if="loadingMySessions" class="grid gap-2 px-4 py-4">
          <USkeleton v-for="index in 5" :key="index" class="h-16 rounded-md" />
        </div>

        <UAlert
          v-else-if="mySessionsError"
          title="Could not load recent sessions"
          :description="mySessionsError"
          icon="i-hugeicons-alert-02"
          color="error"
          variant="subtle"
          class="m-4"
        />

        <UEmpty
          v-else-if="sessionRows.length === 0"
          icon="i-hugeicons-chat-user-01"
          title="No recent sessions"
          description="No agent conversations recorded yet."
          class="px-6 py-16"
        />

        <UListbox
          v-else
          :model-value="undefined"
          value-key="value"
          :items="sessionRows"
          :highlight-on-hover="false"
          class="h-96"
          :ui="{
            root: 'ring-0 rounded-none',
            content: 'max-h-none',
            group: 'p-0',
            item: 'px-4 py-2 data-disabled:cursor-default data-disabled:opacity-100',
          }"
        >
          <template #item="{ item }">
            <div
              class="flex w-full min-w-0 items-start justify-between gap-3 text-left"
            >
              <div class="min-w-0 flex-1">
                <p class="truncate font-mono tabular-nums text-highlighted">
                  {{ item.tokenCountLabel }}
                </p>
                <p class="mt-1 truncate text-xs text-dimmed">
                  {{ item.startedAtLabel }}
                </p>
              </div>

              <div class="flex shrink-0 items-center gap-2">
                <UBadge
                  :label="item.harness"
                  color="neutral"
                  variant="subtle"
                  size="sm"
                />
                <UBadge
                  :label="item.costLabel"
                  color="neutral"
                  variant="subtle"
                  size="sm"
                  class="font-mono font-bold tabular-nums"
                />
              </div>
            </div>
          </template>
        </UListbox>
      </UCard>

      <MemberSessionsSlideover
        v-model:open="mySlideoverOpen"
        :member-name="myDisplayName"
        :conversations="memberConversations"
        :loading="loadingMemberConversations"
        :error="memberConversationsError"
        @minimum-cost-change="onMinimumCostChange"
        @after-leave="mySlideoverOpen = false"
      />

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Token usage by model</span>
        </template>
        <MetricChart
          :option="tokenByModelOption"
          :loading="loadingMyAgentTokenByModelSeries"
          :empty="myAgentTokenByModelSeries.length === 0"
        />
      </UCard>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header>
          <span class="font-medium">Tool usage by tool name</span>
        </template>
        <MetricChart
          :option="toolDonutOption"
          :loading="loadingMyToolCallByToolSeries"
          :empty="myToolCallByToolSeries.length === 0"
          legend-size="md"
        />
      </UCard>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useColorMode } from "@vueuse/core";
import type { ListboxItem, TabsItem } from "@nuxt/ui";
import type {
  AgentConversationListItemDto,
  MetricLeaderboardItem,
  MetricSeriesPoint,
} from "@/api/generated";
import {
  formatMetricMillions,
  metricWindowRangeMs,
  toMetricNumber,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import {
  formatFullDateTime,
  formatTokenCount,
  formatUsd,
  toApiNumber,
} from "@/views/sessions/session-display";
import MetricChart from "./MetricChart.vue";
import MemberSessionsSlideover from "./MemberSessionsSlideover.vue";
import {
  leaderboardBarOption,
  pivotByBucket,
  timeSeriesOption,
  toolCallDonutOption,
} from "./chart-options";

const props = defineProps<{
  myDocumentLeaderboard: MetricLeaderboardItem[];
  mySectionLeaderboard: MetricLeaderboardItem[];
  mySnippetLeaderboard: MetricLeaderboardItem[];
  loadingMyDocumentLeaderboard: boolean;
  loadingMySectionLeaderboard: boolean;
  loadingMySnippetLeaderboard: boolean;
  myAgentTokenByModelSeries: MetricSeriesPoint[];
  loadingMyAgentTokenByModelSeries: boolean;
  myToolCallByToolSeries: MetricSeriesPoint[];
  loadingMyToolCallByToolSeries: boolean;
  mySessions: AgentConversationListItemDto[];
  loadingMySessions: boolean;
  mySessionsError: string | null;
  // Drill-down slideover: same shared sessions-store slice AgentUsageTab.vue's own
  // MemberSessionsSlideover instance uses, just always scoped to the signed-in user.
  myDisplayName: string | null;
  memberConversations: AgentConversationListItemDto[];
  loadingMemberConversations: boolean;
  memberConversationsError: string | null;
  window: MetricWindowToken;
}>();

const emits = defineEmits<{
  /** Asks the root view to (re)load the drill-down slideover at this cost floor. */
  loadMemberDrilldown: [minimumCostUsd: number];
}>();

const mySlideoverOpen = ref(false);

function onSeeMoreSessions() {
  mySlideoverOpen.value = true;
  emits("loadMemberDrilldown", 0);
}

function onMinimumCostChange(minimumCostUsd: number) {
  emits("loadMemberDrilldown", minimumCostUsd);
}

type KnowledgePanel = "documents" | "sections" | "snippets";

type SessionRowItem = ListboxItem & {
  value: string;
  tokenCountLabel: string;
  startedAtLabel: string;
  harness: string;
  costLabel: string;
  disabled: true;
};

const knowledgePanel = ref<KnowledgePanel>("documents");
const knowledgePanelItems: TabsItem[] = [
  { label: "Documents", value: "documents" },
  { label: "Sections", value: "sections" },
  { label: "Snippets", value: "snippets" },
];
const compactTabsUi = {
  list: "h-7 w-auto p-0.5",
  trigger: "h-6 grow-0 px-2 py-0 text-xs",
};

const colorMode = useColorMode();
const isDark = computed(() => colorMode.value === "dark");

/** Picks the leaderboard array matching the active toggle. */
const activeLeaderboardItems = computed<MetricLeaderboardItem[]>(() => {
  switch (knowledgePanel.value) {
    case "sections":
      return props.mySectionLeaderboard;
    case "snippets":
      return props.mySnippetLeaderboard;
    case "documents":
    default:
      return props.myDocumentLeaderboard;
  }
});

/** Loading flag for whichever leaderboard the toggle currently shows. */
const activeLeaderboardLoading = computed(() => {
  switch (knowledgePanel.value) {
    case "sections":
      return props.loadingMySectionLeaderboard;
    case "snippets":
      return props.loadingMySnippetLeaderboard;
    case "documents":
    default:
      return props.loadingMyDocumentLeaderboard;
  }
});

const leaderboardOption = computed(() =>
  leaderboardBarOption(activeLeaderboardItems.value),
);

/** Same row shape as MemberSessionsSlideover.vue, without the cost-filter header. */
const sessionRows = computed<SessionRowItem[]>(() =>
  props.mySessions.map((conversation) => {
    const tokenCountLabel = `${formatTokenCount(
      toApiNumber(conversation.totalInputTokens) +
        toApiNumber(conversation.totalOutputTokens),
    )} tokens`;

    return {
      value: conversation.id,
      label: tokenCountLabel,
      tokenCountLabel,
      startedAtLabel: formatFullDateTime(conversation.startedAtUtc),
      harness: conversation.harness,
      costLabel: formatUsd(conversation.totalCostUsd),
      disabled: true,
    };
  }),
);

const tokenMillionAxisOptions = {
  yAxisName: "Tokens (M)",
  yAxisLabelFormatter: formatMetricMillions,
};

const tokenByModelOption = computed(() =>
  timeSeriesOption(
    pivotByBucket(
      props.myAgentTokenByModelSeries,
      (point) => point.bucket,
      (point) => point.seriesKey,
      (point) => toMetricNumber(point.value),
      metricWindowRangeMs(props.window),
    ),
    tokenMillionAxisOptions,
  ),
);

const toolDonutOption = computed(() =>
  toolCallDonutOption(props.myToolCallByToolSeries, isDark.value, 20),
);
</script>
