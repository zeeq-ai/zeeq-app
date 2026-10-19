<template>
  <ZeeqView id="home" title="Home">
    <!--
    Toolbar (outside the tabs so it persists across tab switches): shared
    reporting-period selector + auto-refresh cadence + manual refresh + last-updated.
    Matches the toolbar pattern used by other views (e.g. Libraries.vue).
    -->
    <template #toolbar>
      <div class="flex w-full flex-wrap items-center justify-between gap-3">
        <MetricsWindowSelect
          :period="period"
          :recent-ranges="recentRanges"
          :today-utc="todayUtc"
          @select="onPeriodSelect"
          @editing-change="editorOpen = $event"
        />

        <div class="flex items-center gap-3">
          <span v-if="historicalPeriod" class="text-sm text-muted">
            Historical period · manual refresh
          </span>
          <span v-if="lastUpdatedAt" class="text-sm text-muted">
            Updated {{ updatedAgo }}
          </span>
          <USelect
            v-model="autoRefreshMode"
            :items="autoRefreshItems"
            icon="i-hugeicons-refresh"
            class="w-40"
          />
          <UButton
            icon="i-hugeicons-refresh"
            color="neutral"
            variant="subtle"
            :loading="refreshing"
            aria-label="Refresh metrics"
            @click="refreshNow"
          />
        </div>
      </div>
    </template>

    <div class="flex flex-col gap-4">
      <!-- Full UTC dates, partial-today / historical state and the generic older-telemetry note. -->
      <MetricPeriodNotice :period="period" :today-utc="todayUtc" />

      <!-- Failed panels of the current period; same-period panels keep their last good data. -->
      <UAlert
        v-if="error"
        color="error"
        variant="subtle"
        icon="i-hugeicons-alert-02"
        :title="error"
        description="Panels that loaded earlier for this period keep their last successful data."
      />

      <!--
      Pointer drag on a chart pauses auto-refresh so a tick never yanks a
      dataZoom brush out from under the user; release/leave resumes it.
      -->
      <div
        @pointerdown="beginInteraction"
        @pointerup="endInteraction"
        @mouseleave="endInteraction"
      >
        <UTabs v-model="activeTab" :items="tabItems" variant="link">
          <template #overview>
            <OverviewTab
              :overview="overview"
              :loading="isLoading('overview')"
              :tool-call-series="toolCallSeries"
              :user-agent-series="userAgentSeries"
              :tool-call-by-tool-series="toolCallByToolSeries"
              :loading-tool-calls="isLoading('toolCallSeries')"
              :loading-user-agents="isLoading('userAgentSeries')"
              :loading-tool-call-by-tool="isLoading('toolCallByToolSeries')"
              :users="filterUsers"
              :user-items="userItems"
              :tools="filterTools"
              :tool-items="toolItems"
              :domain="chartDomain"
              :period="period"
              :recent-ranges="recentRanges"
              :today-utc="todayUtc"
              :finding-review-items="findingReviewItems"
              :finding-review-next-cursor="findingReviewNextCursor"
              :finding-reviews-loading="findingReviewsLoading"
              :finding-reviews-loading-more="findingReviewsLoadingMore"
              @update:users="onUsersChange"
              @update:tools="onToolsChange"
              @load-finding-reviews="onLoadFindingReviews"
              @load-more-finding-reviews="onLoadMoreFindingReviews"
              @range-used="recordRecentRange"
            />
          </template>

          <template #me>
            <MeTab
              :my-document-leaderboard="myLeaderboard"
              :my-section-leaderboard="mySectionLeaderboard"
              :my-snippet-leaderboard="mySnippetLeaderboard"
              :loading-my-document-leaderboard="isLoading('myLeaderboard')"
              :loading-my-section-leaderboard="
                isLoading('mySectionLeaderboard')
              "
              :loading-my-snippet-leaderboard="
                isLoading('mySnippetLeaderboard')
              "
              :my-agent-token-by-model-series="myAgentTokenByModelSeries"
              :loading-my-agent-token-by-model-series="
                isLoading('myAgentTokenByModelSeries')
              "
              :my-tool-call-by-tool-series="myToolCallByToolSeries"
              :loading-my-tool-call-by-tool-series="
                isLoading('myToolCallByToolSeries')
              "
              :my-sessions="mySessions"
              :loading-my-sessions="loadingMySessions"
              :my-sessions-error="mySessionsError"
              :my-display-name="myDisplayName"
              :member-conversations="memberConversations"
              :loading-member-conversations="loadingMemberConversations"
              :member-conversations-error="memberConversationsError"
              :domain="chartDomain"
              @load-member-drilldown="onLoadMyMemberDrilldown"
            />
          </template>

          <template #reviews>
            <CodeReviewsTab
              :review-findings-by-repo="reviewFindingsByRepo"
              :review-findings-by-origin="reviewFindingsByOrigin"
              :review-volume="reviewVolume"
              :loading-findings="
                isLoading('reviewFindingsByRepo') ||
                isLoading('reviewFindingsByOrigin')
              "
              :loading-volume="isLoading('reviewVolume')"
              :origin="filterOrigin ?? allFilterValue"
              :origin-items="originItems"
              :repository-ids="filterRepositoryIds"
              :repository-items="repositoryItems"
              :author-logins="filterAuthorLogins"
              :author-items="authorItems"
              :domain="chartDomain"
              :review-volume-group="reviewVolumeGroup"
              @update:origin="onOriginChange"
              @update:repositoryIds="onRepositoriesChange"
              @update:authorLogins="onAuthorsChange"
            />
          </template>

          <template #knowledge>
            <KnowledgeBaseTab
              :document-read-series="documentReadSeries"
              :section-read-series="sectionReadSeries"
              :snippet-read-series="snippetReadSeries"
              :leaderboard="leaderboard"
              :section-leaderboard="sectionLeaderboard"
              :snippet-leaderboard="snippetLeaderboard"
              :loading-reads="loadingReads"
              :loading-leaderboard="isLoading('leaderboard')"
              :loading-section-leaderboard="isLoading('sectionLeaderboard')"
              :loading-snippet-leaderboard="isLoading('snippetLeaderboard')"
              :library="knowledgeLibrary ?? allFilterValue"
              :library-items="libraryItems"
              :domain="chartDomain"
              @update:library="onLibraryChange"
            />
          </template>

          <template #prompts>
            <PromptMetricsTab
              :prompt-get-by-user-series="promptGetByUserSeries"
              :prompt-get-by-library-series="promptGetByLibrarySeries"
              :prompt-get-by-user-agent-series="promptGetByUserAgentSeries"
              :prompt-leaderboard="promptLeaderboard"
              :loading-by-user="isLoading('promptUsage')"
              :loading-by-library="isLoading('promptUsage')"
              :loading-by-client="isLoading('promptUsage')"
              :loading-leaderboard="isLoading('promptLeaderboard')"
              :users="promptFilterUsers"
              :user-items="userItems"
              :library="promptLibrary ?? allFilterValue"
              :library-items="libraryItems"
              :domain="chartDomain"
              @update:users="onPromptUsersChange"
              @update:library="onPromptLibraryChange"
            />
          </template>

          <template #agents>
            <AgentUsageTab
              :agent-token-by-model-series="agentTokenByModelSeries"
              :agent-token-by-user-series="agentTokenByUserSeries"
              :agent-token-by-model-user-series="agentTokenByModelUserSeries"
              :agent-cost-usd-series="agentCostUsdSeries"
              :loading-token-by-model="isLoading('agentUsage')"
              :loading-token-by-model-user="isLoading('agentUsage')"
              :loading-token-by-user="isLoading('agentUsage')"
              :loading-cost-usd="isLoading('agentUsage')"
              :members="organizationMembers"
              :loading-members="membersLoading"
              :member-conversations="memberConversations"
              :loading-member-conversations="loadingMemberConversations"
              :member-conversations-error="memberConversationsError"
              :domain="chartDomain"
              @select-member="onSelectAgentUsageMember"
              @close-member-sessions="sessionsStore.clearMemberConversations"
            />
          </template>

          <template #performance>
            <PerformanceCostTab
              :duration-percentiles="durationPercentiles"
              :duration-scatter="durationScatter"
              :review-cost-metrics="reviewCostMetrics"
              :loading-percentiles="loadingPercentiles"
              :loading-scatter="loadingScatter"
              :loading-costs="isLoading('reviewCostMetrics')"
              :domain="chartDomain"
            />
          </template>
        </UTabs>
      </div>
    </div>
  </ZeeqView>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useTimeAgo } from "@vueuse/core";
import {
  codeReviewRequestOriginEnum,
  type FindingSeverity,
} from "@/api/generated";
import { histogramMetricType, useMetricsStore } from "@/stores/metrics-store";
import {
  isHistoricalPeriod,
  metricPeriodKey,
  type MetricDateRange,
  type MetricPeriod,
} from "@/stores/metric-period";
import { throwFirstRejection } from "@/utils/settled";
import { useAppStore } from "@/stores/app-store";
import { useLibraryStore } from "@/stores/library-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import { useSessionsStore } from "@/stores/sessions-store";
import MetricsWindowSelect from "./MetricsWindowSelect.vue";
import MetricPeriodNotice from "./MetricPeriodNotice.vue";
import OverviewTab from "./OverviewTab.vue";
import MeTab from "./MeTab.vue";
import CodeReviewsTab from "./CodeReviewsTab.vue";
import KnowledgeBaseTab from "./KnowledgeBaseTab.vue";
import PromptMetricsTab from "./PromptMetricsTab.vue";
import AgentUsageTab from "./AgentUsageTab.vue";
import PerformanceCostTab from "./PerformanceCostTab.vue";
import {
  autoRefreshItems,
  useMetricsAutoRefresh,
} from "./useMetricsAutoRefresh";
import { repositoryLabel } from "./repository-labels";
import { useUtcToday } from "./useUtcToday";

// Root view is the only store consumer; children receive data as props.
const toast = useToast();
const appStore = useAppStore();
const metricsStore = useMetricsStore();
const { isLoading } = metricsStore;
const {
  period,
  recentRanges,
  chartDomain,
  overview,
  toolCallSeries,
  userAgentSeries,
  toolCallByToolSeries,
  documentReadSeries,
  sectionReadSeries,
  snippetReadSeries,
  leaderboard,
  sectionLeaderboard,
  snippetLeaderboard,
  promptGetByUserSeries,
  promptGetByLibrarySeries,
  promptGetByUserAgentSeries,
  promptLeaderboard,
  reviewVolume,
  reviewFindingsByRepo,
  reviewFindingsByOrigin,
  findingReviewItems,
  findingReviewNextCursor,
  agentTokenByModelSeries,
  agentTokenByUserSeries,
  agentTokenByModelUserSeries,
  agentCostUsdSeries,
  reviewCostMetrics,
  percentilesByMetric,
  scatterByMetric,
  myLeaderboard,
  mySectionLeaderboard,
  mySnippetLeaderboard,
  myAgentTokenByModelSeries,
  myToolCallByToolSeries,
  loading,
  error,
  filterOrigin,
  knowledgeLibrary,
  promptFilterUsers,
  promptLibrary,
  filterUsers,
  filterTools,
  filterRepositoryIds,
  filterAuthorLogins,
  reviewVolumeGroup,
  filterOptionUsers,
  filterOptionTools,
  filterOptionRepositories,
  filterOptionAuthors,
} = storeToRefs(metricsStore);

// Library filter options come from the org's libraries (value = library name,
// which is what the read counters tag). Loaded once on mount. A non-empty
// sentinel is used for "all" because the Select forbids an empty-string value.
const allFilterValue = "__all__";
const libraryStore = useLibraryStore();
const { libraries } = storeToRefs(libraryStore);
const organizationSettingsStore = useOrganizationSettingsStore();
const { members: organizationMembers, membersLoading } = storeToRefs(
  organizationSettingsStore,
);
const sessionsStore = useSessionsStore();
const {
  memberConversations,
  loadingMemberConversations,
  memberConversationsError,
  mySessions,
  loadingMySessions,
  mySessionsError,
} = storeToRefs(sessionsStore);

/** Display name for the "Me" tab's drill-down slideover title. */
const myDisplayName = computed(
  () => appStore.user?.name ?? appStore.user?.email ?? null,
);
const libraryItems = computed(() => [
  { label: "All libraries", value: allFilterValue },
  ...libraries.value.map((library) => ({
    label: library.name,
    value: library.name,
  })),
]);

// Static request-origin options for the Code Reviews tab.
const originItems = [
  { label: "All origins", value: allFilterValue },
  {
    label: "Repository webhook",
    value: codeReviewRequestOriginEnum.RepositoryWebhook,
  },
  { label: "Agent", value: codeReviewRequestOriginEnum.Agent },
  { label: "Manual", value: codeReviewRequestOriginEnum.Manual },
];

// Multi-select filter options, sourced from the filter-options endpoint (loaded
// on mount). Users/tools/authors map name→value directly; repos map display
// name→id so the query filters by id while showing readable names.
const userItems = computed(() =>
  filterOptionUsers.value.map((user) => ({ label: user, value: user })),
);
const toolItems = computed(() =>
  filterOptionTools.value.map((tool) => ({ label: tool, value: tool })),
);
const authorItems = computed(() =>
  filterOptionAuthors.value.map((author) => ({
    label: author,
    value: author,
  })),
);
const repositoryItems = computed(() => {
  const repositories = filterOptionRepositories.value;
  // Suffix repos that share a display name (e.g. a removed + re-added mapping)
  // with a short id so the filter options are distinguishable.
  const nameCounts = new Map<string, number>();
  for (const repository of repositories) {
    const label = repositoryLabel(repository.displayName);
    nameCounts.set(label, (nameCounts.get(label) ?? 0) + 1);
  }
  return repositories.map((repository) => {
    const label = repositoryLabel(repository.displayName);
    return {
      label:
        (nameCounts.get(label) ?? 0) > 1
          ? `${label} (${repository.id.slice(-8)})`
          : label,
      value: repository.id,
    };
  });
});

const tabItems = [
  {
    label: "Overview",
    icon: "i-hugeicons-dashboard-square-01",
    slot: "overview",
    value: "overview",
  },
  {
    label: "Me",
    icon: "i-hugeicons-ai-user",
    slot: "me",
    value: "me",
  },
  {
    label: "Code Reviews",
    icon: "i-hugeicons-git-pull-request",
    slot: "reviews",
    value: "reviews",
  },
  {
    label: "Knowledge Base",
    icon: "i-hugeicons-ai-book",
    slot: "knowledge",
    value: "knowledge",
  },
  {
    label: "Skills",
    icon: "i-hugeicons-ai-file",
    slot: "prompts",
    value: "prompts",
  },
  {
    label: "Sessions",
    icon: "i-hugeicons-chat-spark-01",
    slot: "agents",
    value: "agents",
  },
  {
    label: "Review Throughput",
    icon: "i-hugeicons-dashboard-speed-01",
    slot: "performance",
    value: "performance",
  },
];

const activeTab = ref<string>("overview");
const durationKey = histogramMetricType.reviewDuration;

const sessionTelemetryDocsUrl =
  "https://zeeq.ai/docs/key-features/session-telemetry";
const sessionTelemetryToastId = "session-telemetry-empty";

/** True once every Sessions-tab series has loaded and come back empty (no telemetry recorded). */
const isSessionTelemetryEmpty = computed(
  () =>
    agentTokenByModelSeries.value.length === 0 &&
    agentTokenByUserSeries.value.length === 0 &&
    agentTokenByModelUserSeries.value.length === 0 &&
    agentCostUsdSeries.value.length === 0,
);

// Set by the activeTab watcher below when the user switches into the Sessions tab, and consumed
// (and cleared) by loadActiveTab's "agents" case once that switch's load resolves — so the
// empty-telemetry toast fires once per switch, not on every periodic auto-refresh tick while the
// user is already sitting on the tab (loadActiveTab also runs on the refresh interval, which
// never touches `activeTab`).
let sessionsTabSwitch = false;

watch(activeTab, (tab, previousTab) => {
  if (tab === "agents" && tab !== previousTab) {
    sessionsTabSwitch = true;
  }
});

/** UI-8 duration percentiles for the current window, keyed by metric type. */
const durationPercentiles = computed(
  () => percentilesByMetric.value[durationKey] ?? [],
);
const durationScatter = computed(
  () => scatterByMetric.value[durationKey] ?? [],
);

// Derived loading flags for panels backed by multiple / keyed store loads.
const loadingReads = computed(() => isLoading("knowledgeReads"));
const loadingPercentiles = computed(() =>
  isLoading(`percentiles:${durationKey}`),
);
const loadingScatter = computed(() => isLoading(`scatter:${durationKey}`));

/** First-page loading state for the findings drill-down slideover, keyed by severity. */
const findingReviewsLoading = computed(() => ({
  Critical: loading.value["findingReviews:Critical"] ?? false,
  Major: loading.value["findingReviews:Major"] ?? false,
}));

/** "Load more" loading state for the findings drill-down slideover, keyed by severity. */
const findingReviewsLoadingMore = computed(() => ({
  Critical: loading.value["findingReviews:Critical:more"] ?? false,
  Major: loading.value["findingReviews:Major:more"] ?? false,
}));

/** Identity (org + period + tab) a batch started under; compared after awaits to detect staleness. */
function toastIdentity(): string {
  return `${metricsStore.activeOrganizationId}|${metricPeriodKey(period.value)}|${activeTab.value}`;
}

/**
 * Loads only the panels the active tab shows (re-fetch on tab return is fine). Every request
 * in the batch is allowed to settle before any failure is rethrown, so a queued reload never
 * starts while an old sibling is still in flight.
 */
async function loadActiveTab() {
  switch (activeTab.value) {
    case "overview":
      throwFirstRejection(
        await Promise.allSettled([
          metricsStore.loadOverview(),
          metricsStore.loadToolCallSeries(),
          metricsStore.loadUserAgentSeries(),
          metricsStore.loadToolCallByToolSeries(),
        ]),
      );
      break;
    case "me":
      throwFirstRejection(
        await Promise.allSettled([
          metricsStore.loadMyKnowledgeLeaderboards(),
          metricsStore.loadMyAgentTokenByModelSeries(),
          metricsStore.loadMyToolCallByToolSeries(),
          sessionsStore.loadMySessions(),
        ]),
      );
      break;
    case "reviews":
      throwFirstRejection(
        await Promise.allSettled([
          metricsStore.loadReviewFindings(),
          metricsStore.loadReviewVolume(),
        ]),
      );
      break;
    case "knowledge":
      throwFirstRejection(
        await Promise.allSettled([
          metricsStore.loadKnowledgeSeries(),
          metricsStore.loadLeaderboard(),
          metricsStore.loadSectionLeaderboard(),
        ]),
      );
      break;
    case "prompts":
      throwFirstRejection(
        await Promise.allSettled([
          metricsStore.loadPromptUsageSeries(),
          metricsStore.loadPromptLeaderboard(),
        ]),
      );
      break;
    case "agents": {
      // A stale store request returns without committing, so after the await the arrays may be
      // the *cleared* state of a newer org/period/tab rather than this batch's result. Only
      // react if the batch's identity is still the live one.
      const batchIdentity = toastIdentity();
      throwFirstRejection(
        await Promise.allSettled([
          metricsStore.loadAgentUsageSeries(),
          organizationSettingsStore.ensureMembersLoaded(),
        ]),
      );
      const wasSwitch = sessionsTabSwitch;
      sessionsTabSwitch = false;
      // "Never recorded" can only be inferred for a rolling preset: an empty custom interval
      // (especially a historical one) may just predate retained telemetry, so it relies on the
      // empty state + coverage notice instead of this setup prompt.
      if (
        wasSwitch &&
        period.value.mode === "relative" &&
        batchIdentity === toastIdentity() &&
        isSessionTelemetryEmpty.value
      ) {
        // Stable id: useToast().add() merges into an existing toast with the same id (resetting
        // its 30s duration) instead of stacking a second, identical toast — so a still-open
        // toast from a prior visit gets refreshed rather than duplicated. (Don't call
        // toast.remove() first: its 200ms delayed removal is id-based and unconditional, so it
        // would delete this freshly re-added toast out from under itself.)
        toast.add({
          id: sessionTelemetryToastId,
          title: "No session telemetry yet",
          description:
            "This organization hasn't recorded any agent session telemetry. Set it up to start capturing usage here.",
          icon: "i-hugeicons-information-circle",
          color: "info",
          duration: 30_000,
          actions: [
            {
              label: "Learn how to set it up",
              to: sessionTelemetryDocsUrl,
              target: "_blank",
            },
          ],
        });
      }
      break;
    }
    case "performance":
      throwFirstRejection(
        await Promise.allSettled([
          metricsStore.loadPercentiles(durationKey),
          metricsStore.loadScatter(durationKey),
          metricsStore.loadReviewCosts(),
        ]),
      );
      break;
  }
}

// UTC "today" re-evaluates at midnight; a range ending before it is a completed, fixed period.
const todayUtc = useUtcToday();
const historicalPeriod = computed(() =>
  isHistoricalPeriod(period.value, todayUtc.value),
);

/** True while the date editor is open (the selector reports it): the timer pauses. */
const editorOpen = ref(false);

// The saved cadence is never rewritten; the effective timer simply stops while editing or when
// the period is historical (nothing new can arrive), and resumes unchanged afterwards.
const timerSuspended = computed(
  () => editorOpen.value || historicalPeriod.value,
);

const {
  mode: autoRefreshMode,
  lastUpdatedAt,
  refreshing,
  refreshNow,
  beginInteraction,
  endInteraction,
} = useMetricsAutoRefresh(loadActiveTab, timerSuspended);

const updatedAgo = useTimeAgo(() => lastUpdatedAt.value ?? Date.now());

/** Applies the Code Reviews origin filter and reloads the active tab. */
function onOriginChange(value: string) {
  filterOrigin.value =
    value === allFilterValue
      ? undefined
      : Object.values(codeReviewRequestOriginEnum).find(
          (origin) => origin === value,
        );
  void refreshNow().catch(() => {});
}

/** Applies the Knowledge library filter and reloads the active tab. */
function onLibraryChange(value: string) {
  knowledgeLibrary.value = value === allFilterValue ? null : value;
  void refreshNow().catch(() => {});
}

/** Applies the Skills user filter and reloads the active tab. */
function onPromptUsersChange(value: string[]) {
  promptFilterUsers.value = value;
  void refreshNow().catch(() => {});
}

/** Applies the Skills library filter and reloads the active tab. */
function onPromptLibraryChange(value: string) {
  promptLibrary.value = value === allFilterValue ? null : value;
  void refreshNow().catch(() => {});
}

/** Loads the "Me" tab's drill-down slideover, always scoped to the signed-in user. */
function onLoadMyMemberDrilldown(minimumCostUsd: number) {
  const userId = appStore.user?.userId;
  if (!userId) {
    return;
  }

  void sessionsStore
    .loadMemberConversations(userId, minimumCostUsd)
    .catch((err: unknown) => {
      toast.add({
        title: "Could not load recent sessions",
        description:
          err instanceof Error ? err.message : "Sessions request failed.",
        icon: "i-hugeicons-alert-02",
        color: "error",
      });
    });
}

/** Loads the dashboard session drill-down without touching the Sessions inbox slice. */
function onSelectAgentUsageMember(userId: string, minimumCostUsd: number) {
  void sessionsStore
    .loadMemberConversations(userId, minimumCostUsd)
    .catch((err: unknown) => {
      toast.add({
        title: "Could not load member conversations",
        description:
          err instanceof Error ? err.message : "Sessions request failed.",
        icon: "i-hugeicons-alert-02",
        color: "error",
      });
    });
}

/** Applies the MCP user filter and reloads the active tab. */
function onUsersChange(value: string[]) {
  filterUsers.value = value;
  void refreshNow().catch(() => {});
}

/** Applies the MCP tool filter and reloads the active tab. */
function onToolsChange(value: string[]) {
  filterTools.value = value;
  void refreshNow().catch(() => {});
}

/** Applies the Code Reviews repository filter and reloads the active tab. */
function onRepositoriesChange(value: string[]) {
  filterRepositoryIds.value = value;
  void refreshNow().catch(() => {});
}

/** Applies the Code Reviews author filter and reloads the active tab. */
function onAuthorsChange(value: string[]) {
  filterAuthorLogins.value = value;
  void refreshNow().catch(() => {});
}

/** Commits a period chosen in the toolbar selector; the period watcher below reloads. */
function onPeriodSelect(next: MetricPeriod) {
  metricsStore.applyPeriod(next);
}

/**
 * Loads a page for the findings drill-down slideover — fired on open and on every tab/period
 * change inside it. `findingsPeriod` is the slideover's own local selection, independent of the
 * dashboard's shared period.
 */
function onLoadFindingReviews(
  severity: FindingSeverity,
  findingsPeriod: MetricPeriod,
) {
  void metricsStore
    .loadFindingReviews(severity, findingsPeriod)
    .catch(() => {});
}

/** Loads the next page for the findings drill-down slideover's active severity/period. */
function onLoadMoreFindingReviews(
  severity: FindingSeverity,
  findingsPeriod: MetricPeriod,
) {
  void metricsStore
    .loadMoreFindingReviews(severity, findingsPeriod)
    .catch(() => {});
}

/** Remembers a range picked in the findings-local selector without changing the dashboard period. */
function recordRecentRange(range: MetricDateRange) {
  metricsStore.recordRecentRange(range);
}

// Load the filter option lists once; failures are non-fatal (dropdowns stay empty).
onMounted(() => {
  void libraryStore.loadLibraryList().catch(() => {});
  void metricsStore.loadFilterOptions().catch(() => {});
});

// Reload when the active tab or period changes (and once on mount). A change while a batch is
// running is queued as one trailing reload (see useMetricsAutoRefresh), never skipped. Filter
// refs are deliberately NOT watched here — each filter change reloads via its own handler's
// refreshNow(). Errors surface via the store's per-panel errors, so swallow here.
watch(
  [activeTab, period],
  () => {
    void refreshNow().catch(() => {});
  },
  { immediate: true },
);

// When UTC midnight completes the last day of a range that was still live, fetch it once more
// so the final day settles; dates never move and the timer then stays suspended.
watch(todayUtc, (_today, endedDay) => {
  if (period.value.mode === "absolute" && period.value.endDate === endedDay) {
    void refreshNow().catch(() => {});
  }
});
</script>
