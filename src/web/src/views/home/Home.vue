<template>
  <ZeeqView id="home" title="Home">
    <!--
    Toolbar (outside the tabs so it persists across tab switches): shared
    window selector + auto-refresh cadence + manual refresh + last-updated.
    Matches the toolbar pattern used by other views (e.g. Libraries.vue).
    -->
    <template #toolbar>
      <div class="flex w-full flex-wrap items-center justify-between gap-3">
        <MetricsWindowSelect v-model="window" />

        <div class="flex items-center gap-3">
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
      <UAlert
        v-if="error"
        color="error"
        variant="subtle"
        icon="i-hugeicons-alert-02"
        :title="error"
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
              :loading="loading['overview'] ?? false"
              :tool-call-series="toolCallSeries"
              :user-agent-series="userAgentSeries"
              :tool-call-by-tool-series="toolCallByToolSeries"
              :loading-tool-calls="loading['toolCallSeries'] ?? false"
              :loading-user-agents="loading['userAgentSeries'] ?? false"
              :loading-tool-call-by-tool="
                loading['toolCallByToolSeries'] ?? false
              "
              :users="filterUsers"
              :user-items="userItems"
              :tools="filterTools"
              :tool-items="toolItems"
              :window="window"
              :finding-review-items="findingReviewItems"
              :finding-review-next-cursor="findingReviewNextCursor"
              :finding-reviews-loading="findingReviewsLoading"
              :finding-reviews-loading-more="findingReviewsLoadingMore"
              @update:users="onUsersChange"
              @update:tools="onToolsChange"
              @load-finding-reviews="onLoadFindingReviews"
              @load-more-finding-reviews="onLoadMoreFindingReviews"
            />
          </template>

          <template #reviews>
            <CodeReviewsTab
              :review-findings-by-repo="reviewFindingsByRepo"
              :review-findings-by-origin="reviewFindingsByOrigin"
              :review-volume="reviewVolume"
              :loading-findings="
                (loading['reviewFindingsByRepo'] ||
                  loading['reviewFindingsByOrigin']) ??
                false
              "
              :loading-volume="loading['reviewVolume'] ?? false"
              :origin="filterOrigin ?? allFilterValue"
              :origin-items="originItems"
              :repository-ids="filterRepositoryIds"
              :repository-items="repositoryItems"
              :author-logins="filterAuthorLogins"
              :author-items="authorItems"
              :window="window"
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
              :loading-leaderboard="loading['leaderboard'] ?? false"
              :loading-section-leaderboard="
                loading['sectionLeaderboard'] ?? false
              "
              :loading-snippet-leaderboard="
                loading['snippetLeaderboard'] ?? false
              "
              :library="knowledgeLibrary ?? allFilterValue"
              :library-items="libraryItems"
              :window="window"
              @update:library="onLibraryChange"
            />
          </template>

          <template #agents>
            <AgentUsageTab
              :agent-token-by-model-series="agentTokenByModelSeries"
              :agent-token-by-user-series="agentTokenByUserSeries"
              :agent-token-by-model-user-series="agentTokenByModelUserSeries"
              :agent-cost-usd-series="agentCostUsdSeries"
              :loading-token-by-model="
                loading['agentTokenByModelSeries'] ?? false
              "
              :loading-token-by-model-user="
                loading['agentTokenByModelUserSeries'] ?? false
              "
              :loading-token-by-user="
                loading['agentTokenByUserSeries'] ?? false
              "
              :loading-cost-usd="loading['agentCostUsdSeries'] ?? false"
              :members="organizationMembers"
              :loading-members="membersLoading"
              :window="window"
            />
          </template>

          <template #performance>
            <PerformanceCostTab
              :duration-percentiles="durationPercentiles"
              :duration-scatter="durationScatter"
              :loading-percentiles="loadingPercentiles"
              :loading-scatter="loadingScatter"
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
import {
  histogramMetricType,
  useMetricsStore,
  type MetricWindowToken,
} from "@/stores/metrics-store";
import { useLibraryStore } from "@/stores/library-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import MetricsWindowSelect from "./MetricsWindowSelect.vue";
import OverviewTab from "./OverviewTab.vue";
import CodeReviewsTab from "./CodeReviewsTab.vue";
import KnowledgeBaseTab from "./KnowledgeBaseTab.vue";
import AgentUsageTab from "./AgentUsageTab.vue";
import PerformanceCostTab from "./PerformanceCostTab.vue";
import {
  autoRefreshItems,
  useMetricsAutoRefresh,
} from "./useMetricsAutoRefresh";
import { repositoryLabel } from "./repository-labels";

// Root view is the only store consumer; children receive data as props.
const toast = useToast();
const metricsStore = useMetricsStore();
const {
  window,
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
  reviewVolume,
  reviewFindingsByRepo,
  reviewFindingsByOrigin,
  findingReviewItems,
  findingReviewNextCursor,
  agentTokenByModelSeries,
  agentTokenByUserSeries,
  agentTokenByModelUserSeries,
  agentCostUsdSeries,
  percentilesByMetric,
  scatterByMetric,
  loading,
  error,
  filterOrigin,
  knowledgeLibrary,
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
    label: "Code Reviews",
    icon: "i-hugeicons-git-pull-request",
    slot: "reviews",
    value: "reviews",
  },
  {
    label: "Knowledge Base",
    icon: "i-hugeicons-book-open-01",
    slot: "knowledge",
    value: "knowledge",
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
const loadingReads = computed(
  () =>
    loading.value["documentReadSeries"] ||
    loading.value["sectionReadSeries"] ||
    loading.value["snippetReadSeries"] ||
    false,
);
const loadingPercentiles = computed(
  () => loading.value[`percentiles:${durationKey}`] ?? false,
);
const loadingScatter = computed(
  () => loading.value[`scatter:${durationKey}`] ?? false,
);

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

/** Loads only the panels the active tab shows (re-fetch on tab return is fine). */
async function loadActiveTab() {
  switch (activeTab.value) {
    case "overview":
      await Promise.all([
        metricsStore.loadOverview(),
        metricsStore.loadToolCallSeries(),
        metricsStore.loadUserAgentSeries(),
        metricsStore.loadToolCallByToolSeries(),
      ]);
      break;
    case "reviews":
      await Promise.all([
        metricsStore.loadReviewFindings(),
        metricsStore.loadReviewVolume(),
      ]);
      break;
    case "knowledge":
      await Promise.all([
        metricsStore.loadKnowledgeSeries(),
        metricsStore.loadLeaderboard(),
        metricsStore.loadSectionLeaderboard(),
      ]);
      break;
    case "agents": {
      await Promise.all([
        metricsStore.loadAgentUsageSeries(),
        organizationSettingsStore.ensureMembersLoaded(),
      ]);
      const wasSwitch = sessionsTabSwitch;
      sessionsTabSwitch = false;
      if (wasSwitch && isSessionTelemetryEmpty.value) {
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
      await Promise.all([
        metricsStore.loadPercentiles(durationKey),
        metricsStore.loadScatter(durationKey),
      ]);
      break;
  }
}

const {
  mode: autoRefreshMode,
  lastUpdatedAt,
  refreshing,
  refreshNow,
  beginInteraction,
  endInteraction,
} = useMetricsAutoRefresh(loadActiveTab);

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

/**
 * Loads a page for the findings drill-down slideover — fired on open and on every tab/period
 * change inside it. `windowToken` is the slideover's own local selection, independent of the
 * dashboard's shared `window`.
 */
function onLoadFindingReviews(
  severity: FindingSeverity,
  windowToken: MetricWindowToken,
) {
  void metricsStore.loadFindingReviews(severity, windowToken).catch(() => {});
}

/** Loads the next page for the findings drill-down slideover's active severity/period. */
function onLoadMoreFindingReviews(
  severity: FindingSeverity,
  windowToken: MetricWindowToken,
) {
  void metricsStore
    .loadMoreFindingReviews(severity, windowToken)
    .catch(() => {});
}

// Load the filter option lists once; failures are non-fatal (dropdowns stay empty).
onMounted(() => {
  void libraryStore.loadLibraryList().catch(() => {});
  void metricsStore.loadFilterOptions().catch(() => {});
});

// Reload when the active tab or window changes (and once on mount). Filter refs
// are deliberately NOT watched here — each filter change reloads via its own
// handler's refreshNow(), so a filter update refreshes exactly once with no
// overlap. Errors surface via the store's `error` ref, so swallow here.
watch(
  [activeTab, window],
  () => {
    void refreshNow().catch(() => {});
  },
  { immediate: true },
);
</script>
