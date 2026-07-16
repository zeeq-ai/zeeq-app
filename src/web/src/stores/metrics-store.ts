import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Metrics,
  metricSeriesGroupEnum,
  reviewVolumeGroupEnum,
  reviewFindingsGroupEnum,
  type CodeReviewRequestOrigin,
  type MetricLeaderboardItem,
  type MetricPercentilePoint,
  type MetricScatterPoint,
  type MetricSeriesGroup,
  type MetricSeriesPoint,
  type MetricsOverview,
  type MetricsRepositoryOption,
  type ReviewFindingsPoint,
  type ReviewVolumeGroup,
  type ReviewVolumePoint,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";

/**
 * Fixed window tokens accepted by every metrics route (server `[AllowedValues]`).
 * Ordered shortest→longest so the selector reads naturally.
 */
export const metricWindowTokens = [
  "15m",
  "30m",
  "1h",
  "4h",
  "12h",
  "24h",
  "7d",
  "14d",
  "30d",
] as const;
export type MetricWindowToken = (typeof metricWindowTokens)[number];

/** Look-back span and fixed bucket width, in milliseconds, for a window token. */
export type MetricWindowRangeMs = { spanMs: number; bucketMs: number };

const minuteMs = 60_000;
const hourMs = 60 * minuteMs;
const dayMs = 24 * hourMs;

/**
 * Client mirror of the backend's `MetricWindowExtensions.ToRange()`
 * (`Zeeq.Core.Models/Metrics/MetricWindow.cs`) — keep in sync. Used to fill
 * in empty buckets on the client (the API only returns buckets that have at
 * least one point), so the x-axis shows the true bucket cadence instead of
 * collapsing empty stretches away.
 */
const metricWindowRangesMs: Record<MetricWindowToken, MetricWindowRangeMs> = {
  "15m": { spanMs: 15 * minuteMs, bucketMs: 1 * minuteMs },
  "30m": { spanMs: 30 * minuteMs, bucketMs: 2 * minuteMs },
  "1h": { spanMs: 1 * hourMs, bucketMs: 5 * minuteMs },
  "4h": { spanMs: 4 * hourMs, bucketMs: 15 * minuteMs },
  "12h": { spanMs: 12 * hourMs, bucketMs: 30 * minuteMs },
  "24h": { spanMs: 24 * hourMs, bucketMs: 1 * hourMs },
  "7d": { spanMs: 7 * dayMs, bucketMs: 6 * hourMs },
  "14d": { spanMs: 14 * dayMs, bucketMs: 12 * hourMs },
  "30d": { spanMs: 30 * dayMs, bucketMs: 1 * dayMs },
};

/** Resolves the look-back span and bucket width (ms) for a window token. */
export function metricWindowRangeMs(
  token: MetricWindowToken,
): MetricWindowRangeMs {
  return metricWindowRangesMs[token];
}
export const defaultMetricWindow: MetricWindowToken = "24h";

/** Counter metric types backing the bucketed series panels (UI-1, UI-2, UI-6). */
export const counterMetricType = {
  toolCall: "zeeq_tool_call_counter",
  userAgent: "zeeq_user_agent_counter",
  documentRead: "zeeq_document_read_counter",
  sectionRead: "zeeq_section_read_counter",
  snippetRead: "zeeq_snippet_read_counter",
} as const;

/** Histogram metric types backing the percentile/scatter panels (UI-8, UI-9). */
export const histogramMetricType = {
  reviewDuration: "zeeq_review_duration_ms",
  reviewTokens: "zeeq_review_tokens",
  reviewCost: "zeeq_review_cost_usd",
  agentTokenUsage: "zeeq_agent_token_usage",
  agentCostUsd: "zeeq_agent_cost_usd",
} as const;
export type HistogramMetricType =
  (typeof histogramMetricType)[keyof typeof histogramMetricType];

/** Human labels for the window selector, keyed by token. */
const windowLabels: Record<MetricWindowToken, string> = {
  "15m": "Last 15 minutes",
  "30m": "Last 30 minutes",
  "1h": "Last hour",
  "4h": "Last 4 hours",
  "12h": "Last 12 hours",
  "24h": "Last 24 hours",
  "7d": "Last 7 days",
  "14d": "Last 14 days",
  "30d": "Last 30 days",
};

/** Ready-made `{ label, value }` items for a Nuxt UI select. */
export const metricWindowItems = metricWindowTokens.map((token) => ({
  label: windowLabels[token],
  value: token,
}));

/**
 * Store for the home-page metrics dashboard (API tag `Metrics`).
 *
 * `Home.vue` is the only consumer: it drives the shared window + per-tab filters,
 * calls the load actions for whichever panels the active tab shows, and pushes
 * the raw DTO arrays into child panels as props. Numeric fields arrive as
 * `number | string` (int64/double JSON), so panels coerce with `toMetricNumber`
 * and wrap bucket timestamps with `new Date(...)` when building view models.
 */
export const useMetricsStore = defineStore("metrics-store", () => {
  const appStore = useAppStore();

  // Shared window across every panel.
  const window = ref<MetricWindowToken>(defaultMetricWindow);

  // Filters are scoped per tab: MCP uses users/tools, Knowledge uses a single
  // library, Code Reviews uses repos/authors/origin, Performance uses facet/repo.
  // Each load passes only its own tab's filters so a filter set on one tab never
  // leaks into another tab's query.
  const filterUsers = ref<string[]>([]);
  const filterTools = ref<string[]>([]);
  const filterRepositoryIds = ref<string[]>([]);
  const filterAuthorLogins = ref<string[]>([]);
  const filterOrigin = ref<CodeReviewRequestOrigin | undefined>(undefined);
  const knowledgeLibrary = ref<string | null>(null);
  const performanceFacet = ref<string | undefined>(undefined);
  const performanceRepositoryId = ref<string | undefined>(undefined);

  // Review-volume grouping dimension the user can flip (repo/author/origin).
  // Findings-by-severity ships both repo and origin groupings side by side
  // (see loadReviewFindings) rather than a single toggleable dimension.
  const reviewVolumeGroup = ref<ReviewVolumeGroup>(reviewVolumeGroupEnum.Repo);

  // Panel data — raw DTOs; view models are computed in the child components.
  const overview = ref<MetricsOverview | null>(null);
  const toolCallSeries = ref<MetricSeriesPoint[]>([]);
  const userAgentSeries = ref<MetricSeriesPoint[]>([]);
  const toolCallByToolSeries = ref<MetricSeriesPoint[]>([]);
  const documentReadSeries = ref<MetricSeriesPoint[]>([]);
  const sectionReadSeries = ref<MetricSeriesPoint[]>([]);
  const snippetReadSeries = ref<MetricSeriesPoint[]>([]);
  const leaderboard = ref<MetricLeaderboardItem[]>([]);
  const sectionLeaderboard = ref<MetricLeaderboardItem[]>([]);
  const snippetLeaderboard = ref<MetricLeaderboardItem[]>([]);
  const reviewVolume = ref<ReviewVolumePoint[]>([]);
  const reviewFindingsByRepo = ref<ReviewFindingsPoint[]>([]);
  const reviewFindingsByOrigin = ref<ReviewFindingsPoint[]>([]);
  const agentTokenByModelSeries = ref<MetricSeriesPoint[]>([]);
  const agentTokenByUserSeries = ref<MetricSeriesPoint[]>([]);
  const agentCostUsdSeries = ref<MetricSeriesPoint[]>([]);
  const percentilesByMetric = ref<Record<string, MetricPercentilePoint[]>>({});
  const scatterByMetric = ref<Record<string, MetricScatterPoint[]>>({});

  // Filter option lists, populated once from the filter-options endpoint and
  // used to build the per-tab dropdowns (values sourced from the data itself).
  const filterOptionUsers = ref<string[]>([]);
  const filterOptionTools = ref<string[]>([]);
  const filterOptionRepositories = ref<MetricsRepositoryOption[]>([]);
  const filterOptionAuthors = ref<string[]>([]);

  // Per-panel loading flags keyed by a stable panel id; single last-error string.
  const loading = ref<Record<string, boolean>>({});
  const error = ref<string | null>(null);

  const activeOrganizationId = computed(
    () =>
      appStore.currentOrganization?.id ?? appStore.user?.organizationId ?? "",
  );
  const anyLoading = computed(() => Object.values(loading.value).some(Boolean));

  /** Loads the headline stat-card numbers (UI-0). */
  async function loadOverview() {
    const orgId = requireOrganizationId();
    await run("overview", async () => {
      overview.value = await Metrics.getMetricsOverview(orgId, {
        window: window.value,
      });
    });
  }

  /** Loads the distinct filter option lists once (users/tools/repositories/authors). */
  async function loadFilterOptions() {
    const orgId = requireOrganizationId();
    await run("filterOptions", async () => {
      const options = await Metrics.getMetricsFilterOptions(orgId);
      filterOptionUsers.value = options.users;
      filterOptionTools.value = options.tools;
      filterOptionRepositories.value = options.repositories;
      filterOptionAuthors.value = options.authors;
    });
  }

  /** Loads the MCP tool-call series grouped by user for the top-N stacked area (UI-1). */
  async function loadToolCallSeries() {
    toolCallSeries.value = await loadSeries(
      "toolCallSeries",
      counterMetricType.toolCall,
      metricSeriesGroupEnum.User,
      mcpFilters(),
    );
  }

  /** Loads the connecting-agent series grouped by user-agent (UI-2). */
  async function loadUserAgentSeries() {
    userAgentSeries.value = await loadSeries(
      "userAgentSeries",
      counterMetricType.userAgent,
      metricSeriesGroupEnum.UserAgent,
      mcpFilters(),
    );
  }

  /** Loads the MCP tool-call series grouped by tool name for the tool-mix donut. */
  async function loadToolCallByToolSeries() {
    toolCallByToolSeries.value = await loadSeries(
      "toolCallByToolSeries",
      counterMetricType.toolCall,
      metricSeriesGroupEnum.Tool,
      mcpFilters(),
    );
  }

  /** Loads document/section/snippet read series grouped by library (UI-6). */
  async function loadKnowledgeSeries() {
    const libraries = knowledgeLibraryFilter();
    const [documents, sections, snippets] = await Promise.all([
      loadSeries(
        "documentReadSeries",
        counterMetricType.documentRead,
        metricSeriesGroupEnum.Library,
        { libraries },
      ),
      loadSeries(
        "sectionReadSeries",
        counterMetricType.sectionRead,
        metricSeriesGroupEnum.Library,
        { libraries },
      ),
      loadSeries(
        "snippetReadSeries",
        counterMetricType.snippetRead,
        metricSeriesGroupEnum.Library,
        { libraries },
      ),
    ]);
    documentReadSeries.value = documents;
    sectionReadSeries.value = sections;
    snippetReadSeries.value = snippets;
  }

  /** Loads the top-N most-read paths (UI-7), scoped to the selected library. */
  async function loadLeaderboard() {
    const orgId = requireOrganizationId();
    await run("leaderboard", async () => {
      leaderboard.value = await Metrics.getMetricLeaderboard(orgId, {
        window: window.value,
        library: knowledgeLibrary.value ?? undefined,
      });
    });
  }

  /**
   * Loads the top-N most-read sections and, separately, the top-N most-read
   * code snippets (path + heading), scoped to the selected library — one
   * level finer than `loadLeaderboard`, so two sections in the same document
   * rank separately. The two kinds are fetched as two calls (never combined
   * server-side): a section and a code snippet under the same heading share
   * an identical (path, heading) pair, so combining them would merge counts
   * for "the explanation" and "the code sample" into one row.
   */
  async function loadSectionLeaderboard() {
    const orgId = requireOrganizationId();
    const library = knowledgeLibrary.value ?? undefined;
    await Promise.all([
      run("sectionLeaderboard", async () => {
        sectionLeaderboard.value = await Metrics.getMetricSectionLeaderboard(
          orgId,
          { window: window.value, kind: "section", library },
        );
      }),
      run("snippetLeaderboard", async () => {
        snippetLeaderboard.value = await Metrics.getMetricSectionLeaderboard(
          orgId,
          { window: window.value, kind: "code", library },
        );
      }),
    ]);
  }

  /** Loads bucketed review volume for the reviews tab (UI-4/UI-5). */
  async function loadReviewVolume() {
    const orgId = requireOrganizationId();
    await run("reviewVolume", async () => {
      reviewVolume.value = await Metrics.getReviewVolume(orgId, {
        window: window.value,
        groupBy: reviewVolumeGroup.value,
        repositoryIds: emptyToUndefined(filterRepositoryIds.value),
        authorLogins: emptyToUndefined(filterAuthorLogins.value),
        origin: filterOrigin.value,
      });
    });
  }

  /**
   * Loads bucketed finding-severity sums for the reviews tab (UI-3), both
   * by repository and by request origin, so both panels render at once
   * rather than behind a single toggleable dimension.
   */
  async function loadReviewFindings() {
    const orgId = requireOrganizationId();
    const repos = emptyToUndefined(filterRepositoryIds.value);
    const authors = emptyToUndefined(filterAuthorLogins.value);
    await Promise.all([
      run("reviewFindingsByRepo", async () => {
        reviewFindingsByRepo.value = await Metrics.getReviewFindings(orgId, {
          window: window.value,
          groupBy: reviewFindingsGroupEnum.Repo,
          repositoryIds: repos,
          authorLogins: authors,
        });
      }),
      run("reviewFindingsByOrigin", async () => {
        reviewFindingsByOrigin.value = await Metrics.getReviewFindings(orgId, {
          window: window.value,
          groupBy: reviewFindingsGroupEnum.Origin,
          repositoryIds: repos,
          authorLogins: authors,
        });
      }),
    ]);
  }

  /**
   * Loads the first agent telemetry panels backed by existing emitters:
   * token usage split by model/user, plus aggregate USD cost over time.
   */
  async function loadAgentUsageSeries() {
    const [tokensByModel, tokensByUser, costUsd] = await Promise.all([
      loadSeries(
        "agentTokenByModelSeries",
        histogramMetricType.agentTokenUsage,
        metricSeriesGroupEnum.Model,
        {},
      ),
      loadSeries(
        "agentTokenByUserSeries",
        histogramMetricType.agentTokenUsage,
        metricSeriesGroupEnum.User,
        {},
      ),
      loadSeries(
        "agentCostUsdSeries",
        histogramMetricType.agentCostUsd,
        metricSeriesGroupEnum.None,
        {},
      ),
    ]);
    agentTokenByModelSeries.value = tokensByModel;
    agentTokenByUserSeries.value = tokensByUser;
    agentCostUsdSeries.value = costUsd;
  }

  /** Loads per-bucket p50/p95/p99 for a histogram metric (UI-8/UI-9). */
  async function loadPercentiles(metricType: HistogramMetricType) {
    const orgId = requireOrganizationId();
    await run(`percentiles:${metricType}`, async () => {
      const points = await Metrics.getMetricPercentiles(orgId, metricType, {
        window: window.value,
        repositoryId: performanceRepositoryId.value,
        facet: performanceFacet.value,
      });
      percentilesByMetric.value = {
        ...percentilesByMetric.value,
        [metricType]: points,
      };
    });
  }

  /** Loads recent raw samples for a duration-vs-tokens scatter (UI-8/UI-9). */
  async function loadScatter(metricType: HistogramMetricType) {
    const orgId = requireOrganizationId();
    await run(`scatter:${metricType}`, async () => {
      const points = await Metrics.getMetricScatter(orgId, metricType, {
        window: window.value,
        repositoryId: performanceRepositoryId.value,
        facet: performanceFacet.value,
      });
      scatterByMetric.value = {
        ...scatterByMetric.value,
        [metricType]: points,
      };
    });
  }

  /** Shared loader for the counter series panels; callers pass only their tab's filters. */
  async function loadSeries(
    key: string,
    metricType: string,
    groupBy: MetricSeriesGroup,
    filters: { users?: string[]; tools?: string[]; libraries?: string[] },
  ): Promise<MetricSeriesPoint[]> {
    const orgId = requireOrganizationId();
    let points: MetricSeriesPoint[] = [];
    await run(key, async () => {
      points = await Metrics.getMetricSeries(orgId, metricType, {
        window: window.value,
        groupBy,
        users: filters.users,
        tools: filters.tools,
        libraries: filters.libraries,
      });
    });
    return points;
  }

  /** MCP-tab series filters (user + tool multi-selects). */
  function mcpFilters() {
    return {
      users: emptyToUndefined(filterUsers.value),
      tools: emptyToUndefined(filterTools.value),
    };
  }

  /** Knowledge-tab library filter as a single-value array, or undefined for all. */
  function knowledgeLibraryFilter(): string[] | undefined {
    return knowledgeLibrary.value ? [knowledgeLibrary.value] : undefined;
  }

  /** True while the named panel is in flight. */
  function isLoading(key: string): boolean {
    return loading.value[key] === true;
  }

  function requireOrganizationId(): string {
    if (!activeOrganizationId.value) {
      throw new Error("Select an organization before viewing metrics.");
    }
    return activeOrganizationId.value;
  }

  /** Wraps a panel load with its loading flag and shared error capture. */
  async function run(key: string, action: () => Promise<void>) {
    loading.value = { ...loading.value, [key]: true };
    error.value = null;
    try {
      await action();
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load metrics.");
      throw err;
    } finally {
      loading.value = { ...loading.value, [key]: false };
    }
  }

  return {
    window,
    filterUsers,
    filterTools,
    filterRepositoryIds,
    filterAuthorLogins,
    filterOrigin,
    knowledgeLibrary,
    performanceFacet,
    performanceRepositoryId,
    reviewVolumeGroup,
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
    agentTokenByModelSeries,
    agentTokenByUserSeries,
    agentCostUsdSeries,
    percentilesByMetric,
    scatterByMetric,
    filterOptionUsers,
    filterOptionTools,
    filterOptionRepositories,
    filterOptionAuthors,
    loading,
    error,
    activeOrganizationId,
    anyLoading,
    isLoading,
    loadOverview,
    loadFilterOptions,
    loadToolCallSeries,
    loadUserAgentSeries,
    loadToolCallByToolSeries,
    loadKnowledgeSeries,
    loadLeaderboard,
    loadSectionLeaderboard,
    loadReviewVolume,
    loadReviewFindings,
    loadAgentUsageSeries,
    loadPercentiles,
    loadScatter,
  };
});

/** Coerces an int64/double JSON field (`number | string`) to a finite number. */
export function toMetricNumber(value: number | string | null): number {
  if (value === null) {
    return 0;
  }
  return typeof value === "number" ? value : Number(value) || 0;
}

/** Drops empty multi-select arrays so they never widen the request/cache key. */
function emptyToUndefined(values: string[]): string[] | undefined {
  return values.length > 0 ? values : undefined;
}

function errorMessage(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useMetricsStore, import.meta.hot));
}
