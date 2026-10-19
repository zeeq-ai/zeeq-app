import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Metrics,
  metricSeriesGroupEnum,
  reviewVolumeGroupEnum,
  reviewFindingsGroupEnum,
  type CodeReviewRequestOrigin,
  type FindingReviewListItemResponse,
  type FindingSeverity,
  type MetricLeaderboardItem,
  type MetricPercentilePoint,
  type MetricScatterPoint,
  type MetricSeriesGroup,
  type MetricSeriesPoint,
  type MetricTwoDimensionalSeriesPoint,
  type MetricsOverview,
  type MetricsRepositoryOption,
  type ReviewFindingsGroup,
  type ReviewFindingsPoint,
  type ReviewCostMetrics,
  type ReviewVolumeGroup,
  type ReviewVolumePoint,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { throwFirstRejection, unwrapSettled } from "@/utils/settled";
import {
  isBucketOutsidePeriod,
  metricPeriodKey,
  metricPeriodQuery,
  resolveChartDomain,
  type MetricPeriod,
  type MetricPeriodQuery,
} from "@/stores/metric-period";
import { sharedMetricPeriodState } from "@/stores/metric-period-state";

// The period model lives in metric-period.ts; re-exported here so existing
// importers of the window tokens/ranges keep a single stable entry point.
export {
  defaultMetricWindow,
  metricWindowItems,
  metricWindowRangeMs,
  metricWindowTokens,
  type MetricWindowRangeMs,
  type MetricWindowToken,
} from "@/stores/metric-period";

/** Counter metric types backing the bucketed series panels (UI-1, UI-2, UI-6). */
export const counterMetricType = {
  toolCall: "zeeq_tool_call_counter",
  userAgent: "zeeq_user_agent_counter",
  documentRead: "zeeq_document_read_counter",
  sectionRead: "zeeq_section_read_counter",
  snippetRead: "zeeq_snippet_read_counter",
  promptGet: "zeeq_prompt_get_counter",
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

/** Scope for a request: stale once its generation/identity has been invalidated. */
type RequestScope = { isCurrent: () => boolean };

/** Snapshot of the applied period taken when a load starts; every panel in a batch reads this. */
type PeriodRequest = RequestScope & {
  period: MetricPeriod;
  query: MetricPeriodQuery;
};

const outOfPeriodMessage =
  "The server returned telemetry outside the selected or elapsed period.";

/** Identity of the first findings page / cursor chain: org + severity are keyed separately. */
type FindingsIdentity = string;

/**
 * Store for the home-page metrics dashboard (API tag `Metrics`).
 *
 * `Home.vue` is the only consumer: it drives the shared reporting period + per-tab
 * filters, calls the load actions for whichever panels the active tab shows, and
 * pushes the raw DTO arrays into child panels as props. Numeric fields arrive as
 * `number | string` (int64/double JSON), so panels coerce with `toMetricNumber`
 * and wrap bucket timestamps with `new Date(...)` when building view models.
 *
 * Staleness model: every period/org/filter change bumps a *generation*. Each load action
 * snapshots the period + generation up front (`beginRequest`) and every write
 * (data, error, loading) is gated on it, so a slow response for an old period can
 * never populate a newly labeled one. A later request for the same panel key
 * also supersedes an earlier one. Panel data is cleared on a generation change
 * and `isLoading` reports true until the first load for the new period starts, so
 * old numbers are never shown under the new label and loading wins over "empty".
 */
export const useMetricsStore = defineStore("metrics-store", () => {
  const appStore = useAppStore();

  // Shared period state (in-memory applied period + persisted preset/history).
  const { period, recentRanges, applyPeriod, recordRecentRange } =
    sharedMetricPeriodState;

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
  const promptFilterUsers = ref<string[]>([]);
  const promptLibrary = ref<string | null>(null);
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
  const promptGetByUserSeries = ref<MetricSeriesPoint[]>([]);
  const promptGetByLibrarySeries = ref<MetricSeriesPoint[]>([]);
  const promptGetByUserAgentSeries = ref<MetricSeriesPoint[]>([]);
  const promptLeaderboard = ref<MetricLeaderboardItem[]>([]);
  const reviewVolume = ref<ReviewVolumePoint[]>([]);
  const reviewFindingsByRepo = ref<ReviewFindingsPoint[]>([]);
  const reviewFindingsByOrigin = ref<ReviewFindingsPoint[]>([]);
  const agentTokenByModelSeries = ref<MetricSeriesPoint[]>([]);
  const agentTokenByUserSeries = ref<MetricSeriesPoint[]>([]);
  const agentTokenByModelUserSeries = ref<MetricTwoDimensionalSeriesPoint[]>(
    [],
  );
  const agentCostUsdSeries = ref<MetricSeriesPoint[]>([]);
  const reviewCostMetrics = ref<ReviewCostMetrics>(emptyReviewCostMetrics());
  const percentilesByMetric = ref<Record<string, MetricPercentilePoint[]>>({});
  const scatterByMetric = ref<Record<string, MetricScatterPoint[]>>({});

  // "Me" tab panel data — always scoped to the signed-in user, independent of the
  // Overview tab's shared user/tool filters.
  const myLeaderboard = ref<MetricLeaderboardItem[]>([]);
  const mySectionLeaderboard = ref<MetricLeaderboardItem[]>([]);
  const mySnippetLeaderboard = ref<MetricLeaderboardItem[]>([]);
  const myAgentTokenByModelSeries = ref<MetricSeriesPoint[]>([]);
  const myToolCallByToolSeries = ref<MetricSeriesPoint[]>([]);

  // Findings drill-down list (Critical/Major stat-card slideover), keyed by severity so both
  // severities can hold independent pages/cursors without one clearing the other. The
  // slideover has its own period override, so these are keyed by (org, severity, period)
  // identity rather than cleared by the dashboard period.
  const findingReviewItems = ref<
    Record<FindingSeverity, FindingReviewListItemResponse[]>
  >({ Critical: [], Major: [] });
  const findingReviewNextCursor = ref<Record<FindingSeverity, string | null>>({
    Critical: null,
    Major: null,
  });
  const findingIdentity: Record<FindingSeverity, FindingsIdentity | null> = {
    Critical: null,
    Major: null,
  };
  const findingSequence: Record<FindingSeverity, number> = {
    Critical: 0,
    Major: 0,
  };

  // Filter option lists, populated once from the filter-options endpoint and
  // used to build the per-tab dropdowns (values sourced from the data itself).
  const filterOptionUsers = ref<string[]>([]);
  const filterOptionTools = ref<string[]>([]);
  const filterOptionRepositories = ref<MetricsRepositoryOption[]>([]);
  const filterOptionAuthors = ref<string[]>([]);

  // Per-panel loading flags and errors keyed by a stable panel id.
  const loading = ref<Record<string, boolean>>({});
  const errors = ref<Record<string, string>>({});

  /** Bumped on every period/org change; requests started under an older value are stale. */
  let generation = 0;
  /** Latest request sequence per panel key, so a newer request supersedes an older one. */
  const latestRequest = new Map<string, number>();
  /** True from a period/org/filter change until the first load for the new generation starts. */
  const awaitingPeriodLoad = ref(false);
  /**
   * Initializes at load start and advances after each accepted response. The effective end
   * is therefore evaluated at response/render time, allowing an in-flight bucket to elapse.
   */
  const domainNowMs = ref(Date.now());

  const activeOrganizationId = computed(
    () =>
      appStore.currentOrganization?.id ?? appStore.user?.organizationId ?? "",
  );
  const anyLoading = computed(() => Object.values(loading.value).some(Boolean));

  /** Chart x-axis domain for the applied period (explicit bounds, bucket and UTC mode). */
  const chartDomain = computed(() =>
    resolveChartDomain(period.value, domainNowMs.value),
  );

  /**
   * Root alert text derived from the panels whose *current* load failed. Clearing or
   * succeeding one panel never erases another panel's failure.
   */
  const error = computed<string | null>(() => {
    const messages = Object.values(errors.value);
    if (messages.length === 0) {
      return null;
    }
    return messages.length === 1
      ? messages[0]
      : `${messages[0]} (${messages.length} panels could not be loaded.)`;
  });

  /**
   * Invalidates everything loaded or in flight for the previous period/org: stale
   * writes are dropped, panel data/loading/errors reset, and `isLoading` reports
   * true until the next load begins so the old numbers are never shown.
   */
  function invalidatePeriodData() {
    generation++;
    clearPanelData();
    loading.value = {};
    errors.value = {};
    awaitingPeriodLoad.value = true;
  }

  // Runs synchronously on the change itself — not on the next flush — so a response that
  // lands between the change and the reload can never be written under the new label.
  watch(period, invalidatePeriodData, { flush: "sync" });

  // A filter change queues a (possibly delayed) reload exactly like a period change, so it
  // invalidates immediately too: an in-flight response fetched under the old filters must
  // not paint — and old-filter numbers must not stay on screen — while the reload waits.
  // This conservatively clears every tab; Home's UTabs unmounts inactive panels and reloads
  // them on return, so inactive-tab data is not promised as a cache.
  watch(
    [
      filterUsers,
      filterTools,
      filterRepositoryIds,
      filterAuthorLogins,
      filterOrigin,
      knowledgeLibrary,
      promptFilterUsers,
      promptLibrary,
      performanceFacet,
      performanceRepositoryId,
      reviewVolumeGroup,
    ],
    invalidatePeriodData,
    { flush: "sync" },
  );

  // Findings pages/cursors are scoped to an org; drop them (and any pending page) on change.
  watch(
    activeOrganizationId,
    () => {
      invalidatePeriodData();
      for (const severity of findingSeverities) {
        findingSequence[severity]++;
        findingIdentity[severity] = null;
      }
      findingReviewItems.value = { Critical: [], Major: [] };
      findingReviewNextCursor.value = { Critical: null, Major: null };
    },
    { flush: "sync" },
  );

  /** Resets every period-scoped panel to its empty state (filters and findings are kept). */
  function clearPanelData() {
    overview.value = null;
    toolCallSeries.value = [];
    userAgentSeries.value = [];
    toolCallByToolSeries.value = [];
    documentReadSeries.value = [];
    sectionReadSeries.value = [];
    snippetReadSeries.value = [];
    leaderboard.value = [];
    sectionLeaderboard.value = [];
    snippetLeaderboard.value = [];
    promptGetByUserSeries.value = [];
    promptGetByLibrarySeries.value = [];
    promptGetByUserAgentSeries.value = [];
    promptLeaderboard.value = [];
    reviewVolume.value = [];
    reviewFindingsByRepo.value = [];
    reviewFindingsByOrigin.value = [];
    agentTokenByModelSeries.value = [];
    agentTokenByUserSeries.value = [];
    agentTokenByModelUserSeries.value = [];
    agentCostUsdSeries.value = [];
    reviewCostMetrics.value = emptyReviewCostMetrics();
    percentilesByMetric.value = {};
    scatterByMetric.value = {};
    myLeaderboard.value = [];
    mySectionLeaderboard.value = [];
    mySnippetLeaderboard.value = [];
    myAgentTokenByModelSeries.value = [];
    myToolCallByToolSeries.value = [];
  }

  /**
   * Starts a period-scoped load: snapshots the applied period and generation so every
   * panel in the batch queries (and may write) under one consistent identity. Every
   * period action calls this first, even ones that return early, so `isLoading`
   * never stays pinned by a batch that has nothing to fetch.
   */
  function beginRequest(): PeriodRequest {
    // Home invokes every sibling action synchronously before Promise.allSettled awaits; the
    // coalescer starts its trailing batch only after all siblings settle. So this clears the
    // placeholder for a new batch, never for an old sibling resuming after an await.
    awaitingPeriodLoad.value = false;
    domainNowMs.value = Date.now();
    const snapshot = period.value;
    const requestGeneration = generation;
    return {
      period: snapshot,
      query: metricPeriodQuery(snapshot),
      isCurrent: () => requestGeneration === generation,
    };
  }

  /** Loads the headline stat-card numbers (UI-0). */
  async function loadOverview() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    await run(
      "overview",
      request,
      () => Metrics.getMetricsOverview(orgId, { ...request.query }),
      (data) => {
        overview.value = data;
      },
    );
  }

  /** Loads the distinct filter option lists once (users/tools/repositories/authors). */
  async function loadFilterOptions() {
    const orgId = requireOrganizationId();
    await run(
      "filterOptions",
      orgScope(orgId),
      () => Metrics.getMetricsFilterOptions(orgId),
      (options) => {
        filterOptionUsers.value = options.users;
        filterOptionTools.value = options.tools;
        filterOptionRepositories.value = options.repositories;
        filterOptionAuthors.value = options.authors;
      },
    );
  }

  /** Loads the MCP tool-call series grouped by user for the top-N stacked area (UI-1). */
  async function loadToolCallSeries() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const filters = mcpFilters();
    await run(
      "toolCallSeries",
      request,
      () =>
        fetchSeries(
          request,
          orgId,
          counterMetricType.toolCall,
          metricSeriesGroupEnum.User,
          filters,
        ),
      (points) => {
        toolCallSeries.value = points;
      },
    );
  }

  /** Loads the connecting-agent series grouped by user-agent (UI-2). */
  async function loadUserAgentSeries() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const filters = mcpFilters();
    await run(
      "userAgentSeries",
      request,
      () =>
        fetchSeries(
          request,
          orgId,
          counterMetricType.userAgent,
          metricSeriesGroupEnum.UserAgent,
          filters,
        ),
      (points) => {
        userAgentSeries.value = points;
      },
    );
  }

  /** Loads the MCP tool-call series grouped by tool name for the tool-mix donut. */
  async function loadToolCallByToolSeries() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const filters = mcpFilters();
    await run(
      "toolCallByToolSeries",
      request,
      () =>
        fetchSeries(
          request,
          orgId,
          counterMetricType.toolCall,
          metricSeriesGroupEnum.Tool,
          filters,
        ),
      (points) => {
        toolCallByToolSeries.value = points;
      },
    );
  }

  /**
   * Loads document/section/snippet read series grouped by library (UI-6). The three
   * counters are one panel group (`knowledgeReads`): fetched together, settled
   * together and committed atomically so the chart never renders 1–2 of 3 series.
   */
  async function loadKnowledgeSeries() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const libraries = knowledgeLibraryFilter();
    await run(
      "knowledgeReads",
      request,
      async () => {
        const [documents, sections, snippets] = await Promise.allSettled([
          fetchSeries(
            request,
            orgId,
            counterMetricType.documentRead,
            metricSeriesGroupEnum.Library,
            { libraries },
          ),
          fetchSeries(
            request,
            orgId,
            counterMetricType.sectionRead,
            metricSeriesGroupEnum.Library,
            { libraries },
          ),
          fetchSeries(
            request,
            orgId,
            counterMetricType.snippetRead,
            metricSeriesGroupEnum.Library,
            { libraries },
          ),
        ]);
        return {
          documents: unwrapSettled(documents),
          sections: unwrapSettled(sections),
          snippets: unwrapSettled(snippets),
        };
      },
      (data) => {
        documentReadSeries.value = data.documents;
        sectionReadSeries.value = data.sections;
        snippetReadSeries.value = data.snippets;
      },
    );
  }

  /** Loads the top-N most-read paths (UI-7), scoped to the selected library. */
  async function loadLeaderboard() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const library = knowledgeLibrary.value ?? undefined;
    await run(
      "leaderboard",
      request,
      () => Metrics.getMetricLeaderboard(orgId, { ...request.query, library }),
      (items) => {
        leaderboard.value = items;
      },
    );
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
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const library = knowledgeLibrary.value ?? undefined;
    const results = await Promise.allSettled([
      run(
        "sectionLeaderboard",
        request,
        () =>
          Metrics.getMetricSectionLeaderboard(orgId, {
            ...request.query,
            kind: "section",
            library,
          }),
        (items) => {
          sectionLeaderboard.value = items;
        },
      ),
      run(
        "snippetLeaderboard",
        request,
        () =>
          Metrics.getMetricSectionLeaderboard(orgId, {
            ...request.query,
            kind: "code",
            library,
          }),
        (items) => {
          snippetLeaderboard.value = items;
        },
      ),
    ]);
    throwFirstRejection(results);
  }

  /**
   * Loads the "Me" tab's document/section/snippet leaderboards, each scoped to the signed-in
   * user via the `users` filter (no library filter — org-wide, matching the tab's scope). Skips
   * the requests entirely when the current identity has no email on record (e.g. an IdP login
   * that never surfaced an email claim), leaving the panels empty rather than sending an
   * unfiltered `users: []` request that would return everyone's data.
   */
  async function loadMyKnowledgeLeaderboards() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const email = currentUserEmail();
    if (!email) {
      if (request.isCurrent()) {
        myLeaderboard.value = [];
        mySectionLeaderboard.value = [];
        mySnippetLeaderboard.value = [];
      }
      return;
    }

    const results = await Promise.allSettled([
      run(
        "myLeaderboard",
        request,
        () =>
          Metrics.getMetricLeaderboard(orgId, {
            ...request.query,
            users: [email],
          }),
        (items) => {
          myLeaderboard.value = items;
        },
      ),
      run(
        "mySectionLeaderboard",
        request,
        () =>
          Metrics.getMetricSectionLeaderboard(orgId, {
            ...request.query,
            kind: "section",
            users: [email],
          }),
        (items) => {
          mySectionLeaderboard.value = items;
        },
      ),
      run(
        "mySnippetLeaderboard",
        request,
        () =>
          Metrics.getMetricSectionLeaderboard(orgId, {
            ...request.query,
            kind: "code",
            users: [email],
          }),
        (items) => {
          mySnippetLeaderboard.value = items;
        },
      ),
    ]);
    throwFirstRejection(results);
  }

  /** Loads the "Me" tab's token-usage-by-model series, scoped to the signed-in user. */
  async function loadMyAgentTokenByModelSeries() {
    const request = beginRequest();
    const email = currentUserEmail();
    if (!email) {
      if (request.isCurrent()) {
        myAgentTokenByModelSeries.value = [];
      }
      return;
    }

    const orgId = requireOrganizationId();
    await run(
      "myAgentTokenByModelSeries",
      request,
      () =>
        fetchSeries(
          request,
          orgId,
          histogramMetricType.agentTokenUsage,
          metricSeriesGroupEnum.Model,
          { users: [email] },
        ),
      (points) => {
        myAgentTokenByModelSeries.value = points;
      },
    );
  }

  /** Loads the "Me" tab's tool-usage-by-tool-name series, scoped to the signed-in user. */
  async function loadMyToolCallByToolSeries() {
    const request = beginRequest();
    const email = currentUserEmail();
    if (!email) {
      if (request.isCurrent()) {
        myToolCallByToolSeries.value = [];
      }
      return;
    }

    const orgId = requireOrganizationId();
    await run(
      "myToolCallByToolSeries",
      request,
      () =>
        fetchSeries(
          request,
          orgId,
          counterMetricType.toolCall,
          metricSeriesGroupEnum.Tool,
          { users: [email] },
        ),
      (points) => {
        myToolCallByToolSeries.value = points;
      },
    );
  }

  /** The signed-in user's email, used to scope every "Me" tab query; null if unavailable. */
  function currentUserEmail(): string | null {
    return appStore.user?.email ?? null;
  }

  /**
   * Loads successful dynamic prompt gets by user, library, and client/agent as one panel
   * group (`promptUsage`) committed atomically.
   */
  async function loadPromptUsageSeries() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const users = emptyToUndefined(promptFilterUsers.value);
    const libraries = promptLibraryFilter();
    await run(
      "promptUsage",
      request,
      async () => {
        const [byUser, byLibrary, byUserAgent] = await Promise.allSettled([
          fetchSeries(
            request,
            orgId,
            counterMetricType.promptGet,
            metricSeriesGroupEnum.User,
            { users, libraries },
          ),
          fetchSeries(
            request,
            orgId,
            counterMetricType.promptGet,
            metricSeriesGroupEnum.Library,
            { users, libraries },
          ),
          fetchSeries(
            request,
            orgId,
            counterMetricType.promptGet,
            metricSeriesGroupEnum.UserAgent,
            { users, libraries },
          ),
        ]);
        return {
          byUser: unwrapSettled(byUser),
          byLibrary: unwrapSettled(byLibrary),
          byUserAgent: unwrapSettled(byUserAgent),
        };
      },
      (data) => {
        promptGetByUserSeries.value = data.byUser;
        promptGetByLibrarySeries.value = data.byLibrary;
        promptGetByUserAgentSeries.value = data.byUserAgent;
      },
    );
  }

  /** Loads the top-N successfully retrieved dynamic prompts. */
  async function loadPromptLeaderboard() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const users = emptyToUndefined(promptFilterUsers.value);
    const library = promptLibrary.value ?? undefined;
    await run(
      "promptLeaderboard",
      request,
      () =>
        Metrics.getPromptLeaderboard(orgId, {
          ...request.query,
          users,
          library,
        }),
      (items) => {
        promptLeaderboard.value = items;
      },
    );
  }

  /** Loads bucketed review volume for the reviews tab (UI-4/UI-5). */
  async function loadReviewVolume() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const groupBy = reviewVolumeGroup.value;
    const repositoryIds = emptyToUndefined(filterRepositoryIds.value);
    const authorLogins = emptyToUndefined(filterAuthorLogins.value);
    const origin = filterOrigin.value;
    await run(
      "reviewVolume",
      request,
      async () =>
        withinPeriod(
          await Metrics.getReviewVolume(orgId, {
            ...request.query,
            groupBy,
            repositoryIds,
            authorLogins,
            origin,
          }),
          (point) => point.bucket,
          request,
        ),
      (points) => {
        reviewVolume.value = points;
      },
    );
  }

  /** Loads review-level costs and bucketed author totals for Review Throughput. */
  async function loadReviewCosts() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    await run(
      "reviewCostMetrics",
      request,
      async () => {
        const costs = await Metrics.getReviewCosts(orgId, { ...request.query });
        const responseNowMs = Date.now();
        withinPeriod(
          costs.byAuthor,
          (point) => point.bucket,
          request,
          responseNowMs,
        );
        withinPeriod(
          costs.reviews,
          (sample) => sample.createdAtUtc,
          request,
          responseNowMs,
        );
        return costs;
      },
      (costs) => {
        reviewCostMetrics.value = costs;
      },
    );
  }

  /**
   * Loads bucketed finding-severity sums for the reviews tab (UI-3), both
   * by repository and by request origin, so both panels render at once
   * rather than behind a single toggleable dimension.
   */
  async function loadReviewFindings() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const repositoryIds = emptyToUndefined(filterRepositoryIds.value);
    const authorLogins = emptyToUndefined(filterAuthorLogins.value);
    const fetchFindings = async (groupBy: ReviewFindingsGroup) =>
      withinPeriod(
        await Metrics.getReviewFindings(orgId, {
          ...request.query,
          groupBy,
          repositoryIds,
          authorLogins,
        }),
        (point) => point.bucket,
        request,
      );
    const results = await Promise.allSettled([
      run(
        "reviewFindingsByRepo",
        request,
        () => fetchFindings(reviewFindingsGroupEnum.Repo),
        (points) => {
          reviewFindingsByRepo.value = points;
        },
      ),
      run(
        "reviewFindingsByOrigin",
        request,
        () => fetchFindings(reviewFindingsGroupEnum.Origin),
        (points) => {
          reviewFindingsByOrigin.value = points;
        },
      ),
    ]);
    throwFirstRejection(results);
  }

  /**
   * Loads the first page of the findings drill-down list for a severity (the Critical/Major
   * stat-card slideover), replacing any previously loaded page for that severity. The slideover
   * has its own period selector that defaults to but does not back-propagate into the shared
   * dashboard period, so callers pass the period explicitly rather than this action reading
   * the store's `period` ref. Starting a first page invalidates any pending page/"load more"
   * for the severity and resets its cursor.
   */
  async function loadFindingReviews(
    severity: FindingSeverity,
    findingsPeriod: MetricPeriod,
  ) {
    const orgId = requireOrganizationId();
    const identity = findingsIdentity(orgId, findingsPeriod);
    const sequence = ++findingSequence[severity];
    findingIdentity[severity] = identity;
    findingReviewItems.value = { ...findingReviewItems.value, [severity]: [] };
    findingReviewNextCursor.value = {
      ...findingReviewNextCursor.value,
      [severity]: null,
    };
    // A pending "load more" for the previous chain is now stale (its scope sequence no
    // longer matches), so its own `finally` will never clear its flag: reset that key's
    // loading/error here and supersede it, or the new chain's cursor would appear
    // permanently loading and an old error would survive the reload.
    const moreKey = `findingReviews:${severity}:more`;
    latestRequest.set(moreKey, (latestRequest.get(moreKey) ?? 0) + 1);
    loading.value = { ...loading.value, [moreKey]: false };
    clearError(moreKey);
    await run(
      `findingReviews:${severity}`,
      findingScope(severity, identity, sequence),
      () =>
        Metrics.listFindingReviews(orgId, {
          ...metricPeriodQuery(findingsPeriod),
          severity,
        }),
      (page) => {
        findingReviewItems.value = {
          ...findingReviewItems.value,
          [severity]: page.items,
        };
        findingReviewNextCursor.value = {
          ...findingReviewNextCursor.value,
          [severity]: page.nextCursor ?? null,
        };
      },
    );
  }

  /**
   * Appends the next page for a severity using its stored cursor. No-op when there is no next
   * page, or when `findingsPeriod` is not the period the current page/cursor was fetched under
   * (a cursor is only valid for its own org/severity/period). A first-page reload that lands
   * while this is pending discards it.
   */
  async function loadMoreFindingReviews(
    severity: FindingSeverity,
    findingsPeriod: MetricPeriod,
  ) {
    const cursor = findingReviewNextCursor.value[severity];
    if (!cursor) {
      return;
    }
    const orgId = requireOrganizationId();
    const identity = findingsIdentity(orgId, findingsPeriod);
    if (findingIdentity[severity] !== identity) {
      return;
    }
    const sequence = findingSequence[severity];
    await run(
      `findingReviews:${severity}:more`,
      findingScope(severity, identity, sequence),
      () =>
        Metrics.listFindingReviews(orgId, {
          ...metricPeriodQuery(findingsPeriod),
          severity,
          cursor,
        }),
      (page) => {
        findingReviewItems.value = {
          ...findingReviewItems.value,
          [severity]: [...findingReviewItems.value[severity], ...page.items],
        };
        findingReviewNextCursor.value = {
          ...findingReviewNextCursor.value,
          [severity]: page.nextCursor ?? null,
        };
      },
    );
  }

  /**
   * Loads the agent telemetry panels backed by existing emitters (token usage split by
   * model/user, plus aggregate USD cost over time) as one panel group (`agentUsage`).
   */
  async function loadAgentUsageSeries() {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    await run(
      "agentUsage",
      request,
      async () => {
        const [tokensByModel, tokensByUser, tokensByModelUser, costUsd] =
          await Promise.allSettled([
            fetchSeries(
              request,
              orgId,
              histogramMetricType.agentTokenUsage,
              metricSeriesGroupEnum.Model,
              {},
            ),
            fetchSeries(
              request,
              orgId,
              histogramMetricType.agentTokenUsage,
              metricSeriesGroupEnum.User,
              {},
            ),
            fetchTwoDimensionalSeries(
              request,
              orgId,
              histogramMetricType.agentTokenUsage,
              metricSeriesGroupEnum.Model,
              metricSeriesGroupEnum.User,
            ),
            fetchSeries(
              request,
              orgId,
              histogramMetricType.agentCostUsd,
              metricSeriesGroupEnum.User,
              {},
            ),
          ]);
        return {
          tokensByModel: unwrapSettled(tokensByModel),
          tokensByUser: unwrapSettled(tokensByUser),
          tokensByModelUser: unwrapSettled(tokensByModelUser),
          costUsd: unwrapSettled(costUsd),
        };
      },
      (data) => {
        agentTokenByModelSeries.value = data.tokensByModel;
        agentTokenByUserSeries.value = data.tokensByUser;
        agentTokenByModelUserSeries.value = data.tokensByModelUser;
        agentCostUsdSeries.value = data.costUsd;
      },
    );
  }

  /** Loads per-bucket p50/p95/p99 for a histogram metric (UI-8/UI-9). */
  async function loadPercentiles(metricType: HistogramMetricType) {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const repositoryId = performanceRepositoryId.value;
    const facet = performanceFacet.value;
    await run(
      `percentiles:${metricType}`,
      request,
      async () =>
        withinPeriod(
          await Metrics.getMetricPercentiles(orgId, metricType, {
            ...request.query,
            repositoryId,
            facet,
          }),
          (point) => point.bucket,
          request,
        ),
      (points) => {
        percentilesByMetric.value = {
          ...percentilesByMetric.value,
          [metricType]: points,
        };
      },
    );
  }

  /** Loads recent raw samples for a duration-vs-tokens scatter (UI-8/UI-9). */
  async function loadScatter(metricType: HistogramMetricType) {
    const request = beginRequest();
    const orgId = requireOrganizationId();
    const repositoryId = performanceRepositoryId.value;
    const facet = performanceFacet.value;
    await run(
      `scatter:${metricType}`,
      request,
      async () =>
        withinPeriod(
          await Metrics.getMetricScatter(orgId, metricType, {
            ...request.query,
            repositoryId,
            facet,
          }),
          (point) => point.createdAtUtc,
          request,
        ),
      (points) => {
        scatterByMetric.value = {
          ...scatterByMetric.value,
          [metricType]: points,
        };
      },
    );
  }

  /** Counter/histogram series fetch, validated to lie inside the selected period. */
  async function fetchSeries(
    request: PeriodRequest,
    orgId: string,
    metricType: string,
    groupBy: MetricSeriesGroup,
    filters: { users?: string[]; tools?: string[]; libraries?: string[] },
  ): Promise<MetricSeriesPoint[]> {
    const points = await Metrics.getMetricSeries(orgId, metricType, {
      ...request.query,
      groupBy,
      users: filters.users,
      tools: filters.tools,
      libraries: filters.libraries,
    });
    return withinPeriod(points, (point) => point.bucket, request);
  }

  /** Bucketed series grouped by two dimensions, validated to lie inside the selected period. */
  async function fetchTwoDimensionalSeries(
    request: PeriodRequest,
    orgId: string,
    metricType: string,
    primaryGroupBy: MetricSeriesGroup,
    secondaryGroupBy: MetricSeriesGroup,
  ): Promise<MetricTwoDimensionalSeriesPoint[]> {
    const points = await Metrics.getMetricTwoDimensionalSeries(
      orgId,
      metricType,
      { ...request.query, primaryGroupBy, secondaryGroupBy },
    );
    return withinPeriod(points, (point) => point.bucket, request);
  }

  /**
   * Fails the panel (rather than silently dropping points and showing partial totals as
   * complete) when the server returns a bucket outside selected dates or the elapsed
   * range at response time. The response clock preserves buckets that elapsed in flight.
   */
  function withinPeriod<T>(
    points: T[],
    dateOf: (point: T) => Date,
    request: PeriodRequest,
    responseNowMs = Date.now(),
  ): T[] {
    const domain = resolveChartDomain(request.period, responseNowMs);
    if (points.some((point) => isBucketOutsidePeriod(dateOf(point), domain))) {
      throw new Error(outOfPeriodMessage);
    }
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

  /** Prompts-tab library filter as a single-value array, or undefined for all. */
  function promptLibraryFilter(): string[] | undefined {
    return promptLibrary.value ? [promptLibrary.value] : undefined;
  }

  /**
   * True while the named panel is in flight — or while the period just changed and the
   * reload has not started yet, so panels show loading instead of empty/old data.
   */
  function isLoading(key: string): boolean {
    return awaitingPeriodLoad.value || loading.value[key] === true;
  }

  function requireOrganizationId(): string {
    if (!activeOrganizationId.value) {
      throw new Error("Select an organization before viewing metrics.");
    }
    return activeOrganizationId.value;
  }

  /** Scope bound only to the organization (filter options, which ignore the period). */
  function orgScope(orgId: string): RequestScope {
    return { isCurrent: () => activeOrganizationId.value === orgId };
  }

  /** Scope bound to a findings page chain: org + severity + period identity and first-page sequence. */
  function findingScope(
    severity: FindingSeverity,
    identity: FindingsIdentity,
    sequence: number,
  ): RequestScope {
    return {
      isCurrent: () =>
        findingSequence[severity] === sequence &&
        findingIdentity[severity] === identity,
    };
  }

  /**
   * Runs one panel load: sets that panel's loading flag, fetches, and commits only if the
   * request is still current *and* is the latest for its key. Stale outcomes — data, errors
   * and loading resets alike — are dropped. A current failure records a per-panel error and
   * rethrows so batch callers can tell the refresh failed.
   */
  async function run<T>(
    key: string,
    scope: RequestScope,
    fetch: () => Promise<T>,
    commit: (data: T) => void,
  ): Promise<void> {
    const sequence = (latestRequest.get(key) ?? 0) + 1;
    latestRequest.set(key, sequence);
    const isLatest = () =>
      scope.isCurrent() && latestRequest.get(key) === sequence;

    loading.value = { ...loading.value, [key]: true };
    clearError(key);
    try {
      const data = await fetch();
      if (isLatest()) {
        commit(data);
        // Advance the chart clock past the response: a range that includes today may legitimately
        // return a bucket newer than the clock captured when the request started (e.g. across
        // an hour boundary). The selected range itself never moves.
        domainNowMs.value = Math.max(domainNowMs.value, Date.now());
      }
    } catch (err: unknown) {
      if (isLatest()) {
        errors.value = {
          ...errors.value,
          [key]: errorMessage(err, "Could not load metrics."),
        };
        throw err;
      }
    } finally {
      if (isLatest()) {
        loading.value = { ...loading.value, [key]: false };
      }
    }
  }

  function clearError(key: string) {
    if (key in errors.value) {
      const { [key]: _cleared, ...remaining } = errors.value;
      errors.value = remaining;
    }
  }

  return {
    period,
    recentRanges,
    applyPeriod,
    recordRecentRange,
    chartDomain,
    filterUsers,
    filterTools,
    filterRepositoryIds,
    filterAuthorLogins,
    filterOrigin,
    knowledgeLibrary,
    promptFilterUsers,
    promptLibrary,
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
    filterOptionUsers,
    filterOptionTools,
    filterOptionRepositories,
    filterOptionAuthors,
    loading,
    errors,
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
    loadPromptUsageSeries,
    loadPromptLeaderboard,
    loadReviewVolume,
    loadReviewCosts,
    loadReviewFindings,
    loadFindingReviews,
    loadMoreFindingReviews,
    loadAgentUsageSeries,
    loadPercentiles,
    loadScatter,
    loadMyKnowledgeLeaderboards,
    loadMyAgentTokenByModelSeries,
    loadMyToolCallByToolSeries,
  };
});

const findingSeverities: FindingSeverity[] = ["Critical", "Major"];

function emptyReviewCostMetrics(): ReviewCostMetrics {
  return { reviews: [], byAuthor: [], hasMoreReviews: false };
}

/** Findings page chains are valid only for one org + period (severity keys the chain itself). */
function findingsIdentity(orgId: string, period: MetricPeriod): string {
  return `${orgId}|${metricPeriodKey(period)}`;
}

/** Coerces an int64/double JSON field (`number | string`) to a finite number. */
export function toMetricNumber(value: number | string | null): number {
  if (value === null) {
    return 0;
  }
  return typeof value === "number" ? value : Number(value) || 0;
}

/** Formats high-volume token counts in millions for compact chart axes. */
export function formatMetricMillions(value: number | string | null): string {
  const millions = toMetricNumber(value) / 1_000_000;

  return Number.isInteger(millions) ? millions.toString() : millions.toFixed(1);
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
