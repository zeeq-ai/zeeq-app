import { computed, ref, watch } from "vue";
import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Metrics,
  metricSeriesGroupEnum,
  type MetricLeaderboardItem,
  type MetricSeriesPoint,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import {
  isBucketOutsidePeriod,
  metricPeriodKey,
  metricPeriodQuery,
  resolveChartDomain,
  type MetricPeriod,
} from "@/stores/metric-period";
import { sharedMetricPeriodState } from "@/stores/metric-period-state";
import { counterMetricType } from "@/stores/metrics-store";
import { unwrapSettled } from "@/utils/settled";

const outOfPeriodMessage =
  "The server returned telemetry outside the selected or elapsed period.";

/** Whether the visible Library metrics should settle a custom range whose final UTC day just ended. */
export function shouldReloadLibraryMetricsOnUtcRollover(
  period: MetricPeriod,
  endedDayUtc: string,
  activeLibraryName: string | null,
  showLibraryMetrics: boolean,
): boolean {
  return (
    showLibraryMetrics &&
    !!activeLibraryName &&
    period.mode === "absolute" &&
    period.endDate === endedDayUtc
  );
}

/**
 * Metrics state for the Libraries root panel.
 *
 * Kept separate from the Home dashboard metrics store so the Libraries route
 * has its own library scope and data without mutating Home's filters/data, but
 * it shares the applied reporting period and recent-range history with Home
 * (`metric-period-state.ts`).
 *
 * Staleness: `loadRequestId` bumps on every `loadMetrics`/`clearMetrics` and on
 * every organization/period change (synchronously), and each panel write — data,
 * error and loading — is gated on it, so an old identity's response can never
 * populate the new one. Data is reset when the (org, library, period) identity
 * changes; a same-identity refresh keeps the last successful data while it reloads.
 */
export const useLibraryMetricsStore = defineStore(
  "library-metrics-store",
  () => {
    const appStore = useAppStore();

    const { period, recentRanges, applyPeriod, recordRecentRange } =
      sharedMetricPeriodState;
    const documentReadSeries = ref<MetricSeriesPoint[]>([]);
    const sectionReadSeries = ref<MetricSeriesPoint[]>([]);
    const snippetReadSeries = ref<MetricSeriesPoint[]>([]);
    const leaderboard = ref<MetricLeaderboardItem[]>([]);
    const sectionLeaderboard = ref<MetricLeaderboardItem[]>([]);
    const snippetLeaderboard = ref<MetricLeaderboardItem[]>([]);
    const loading = ref<Record<string, boolean>>({});
    const errors = ref<Record<string, string>>({});

    /** True from a period change until the next `loadMetrics` starts (old numbers are hidden). */
    const awaitingLoad = ref(false);
    const domainNowMs = ref(Date.now());

    const activeOrganizationId = computed(
      () =>
        appStore.currentOrganization?.id ?? appStore.user?.organizationId ?? "",
    );
    const loadingReads = computed(
      () => awaitingLoad.value || (loading.value.reads ?? false),
    );
    const loadingLeaderboard = computed(
      () => awaitingLoad.value || (loading.value.leaderboard ?? false),
    );
    const loadingSectionLeaderboard = computed(
      () => awaitingLoad.value || (loading.value.sectionLeaderboard ?? false),
    );
    const loadingSnippetLeaderboard = computed(
      () => awaitingLoad.value || (loading.value.snippetLeaderboard ?? false),
    );
    const refreshing = computed(() =>
      Object.values(loading.value).some(Boolean),
    );
    const chartDomain = computed(() =>
      resolveChartDomain(period.value, domainNowMs.value),
    );
    const error = computed<string | null>(() => {
      const messages = Object.values(errors.value);
      if (messages.length === 0) {
        return null;
      }
      return messages.length === 1
        ? messages[0]
        : `${messages[0]} (${messages.length} panels could not be loaded.)`;
    });

    let loadRequestId = 0;
    let loadedIdentity: string | null = null;

    // Invalidate in-flight work on org/period changes before a view reload can be scheduled.
    watch(
      [period, activeOrganizationId],
      () => {
        loadRequestId++;
        loadedIdentity = null;
        resetMetricData();
        awaitingLoad.value = true;
      },
      { flush: "sync" },
    );

    async function loadMetrics(libraryName: string | null) {
      const orgId = activeOrganizationId.value;
      if (!orgId || !libraryName) {
        // clearMetrics invalidates pending work and clears data, errors, loading, and awaitingLoad.
        clearMetrics();
        return;
      }

      const requestId = ++loadRequestId;
      awaitingLoad.value = false;
      domainNowMs.value = Date.now();
      const requestPeriod = period.value;
      const identity = `${orgId}|${libraryName}|${metricPeriodKey(requestPeriod)}`;
      if (identity !== loadedIdentity) {
        resetMetricData();
        loadedIdentity = identity;
      }

      await Promise.all([
        loadReadSeries(orgId, libraryName, requestPeriod, requestId),
        loadLeaderboard(orgId, libraryName, requestPeriod, requestId),
        loadSectionLeaderboard(
          "sectionLeaderboard",
          "section",
          orgId,
          libraryName,
          requestPeriod,
          requestId,
        ),
        loadSectionLeaderboard(
          "snippetLeaderboard",
          "code",
          orgId,
          libraryName,
          requestPeriod,
          requestId,
        ),
      ]);
    }

    function clearMetrics() {
      loadRequestId++;
      loadedIdentity = null;
      awaitingLoad.value = false;
      resetMetricData();
    }

    function resetMetricData() {
      documentReadSeries.value = [];
      sectionReadSeries.value = [];
      snippetReadSeries.value = [];
      leaderboard.value = [];
      sectionLeaderboard.value = [];
      snippetLeaderboard.value = [];
      loading.value = {};
      errors.value = {};
    }

    /** Fails a panel rather than silently dropping buckets outside selected dates or elapsed time. */
    function withinPeriod<T>(
      points: T[],
      dateOf: (point: T) => Date,
      requestPeriod: MetricPeriod,
      responseNowMs = Date.now(),
    ): T[] {
      const domain = resolveChartDomain(requestPeriod, responseNowMs);
      if (
        points.some((point) => isBucketOutsidePeriod(dateOf(point), domain))
      ) {
        throw new Error(outOfPeriodMessage);
      }
      return points;
    }

    /**
     * Loads the document/section/snippet read series together and assigns all
     * three refs in one atomic step. The three counter types are independent
     * requests, so gathering them (all settled, so none keeps running after a
     * sibling fails) before writing any ref prevents the "Reads over time"
     * chart from ever rendering a transient state where only 1-2 of the 3
     * series have arrived.
     */
    async function loadReadSeries(
      orgId: string,
      libraryName: string,
      requestPeriod: MetricPeriod,
      requestId: number,
    ) {
      await run("reads", requestId, async () => {
        const seriesFilters = {
          ...metricPeriodQuery(requestPeriod),
          groupBy: metricSeriesGroupEnum.None,
          libraries: [libraryName],
        };
        const [documents, sections, snippets] = await Promise.allSettled([
          Metrics.getMetricSeries(
            orgId,
            counterMetricType.documentRead,
            seriesFilters,
          ),
          Metrics.getMetricSeries(
            orgId,
            counterMetricType.sectionRead,
            seriesFilters,
          ),
          Metrics.getMetricSeries(
            orgId,
            counterMetricType.snippetRead,
            seriesFilters,
          ),
        ]);
        const responseNowMs = Date.now();
        const documentPoints = withinPeriod(
          unwrapSettled(documents),
          (point) => point.bucket,
          requestPeriod,
          responseNowMs,
        );
        const sectionPoints = withinPeriod(
          unwrapSettled(sections),
          (point) => point.bucket,
          requestPeriod,
          responseNowMs,
        );
        const snippetPoints = withinPeriod(
          unwrapSettled(snippets),
          (point) => point.bucket,
          requestPeriod,
          responseNowMs,
        );

        if (requestId !== loadRequestId) return;
        documentReadSeries.value = documentPoints;
        sectionReadSeries.value = sectionPoints;
        snippetReadSeries.value = snippetPoints;
      });
    }

    async function loadLeaderboard(
      orgId: string,
      libraryName: string,
      requestPeriod: MetricPeriod,
      requestId: number,
    ) {
      await run("leaderboard", requestId, async () => {
        const items = await Metrics.getMetricLeaderboard(orgId, {
          ...metricPeriodQuery(requestPeriod),
          library: libraryName,
          top: 10,
        });

        if (requestId === loadRequestId) {
          leaderboard.value = items;
        }
      });
    }

    async function loadSectionLeaderboard(
      key: "sectionLeaderboard" | "snippetLeaderboard",
      kind: "section" | "code",
      orgId: string,
      libraryName: string,
      requestPeriod: MetricPeriod,
      requestId: number,
    ) {
      await run(key, requestId, async () => {
        const items = await Metrics.getMetricSectionLeaderboard(orgId, {
          ...metricPeriodQuery(requestPeriod),
          library: libraryName,
          kind,
          top: 10,
        });

        if (requestId !== loadRequestId) return;
        if (key === "sectionLeaderboard") {
          sectionLeaderboard.value = items;
        } else {
          snippetLeaderboard.value = items;
        }
      });
    }

    /** Runs one panel load; stale requests never touch loading or error state. */
    async function run(
      key: string,
      requestId: number,
      action: () => Promise<void>,
    ) {
      loading.value = { ...loading.value, [key]: true };
      if (key in errors.value) {
        const { [key]: _cleared, ...remaining } = errors.value;
        errors.value = remaining;
      }
      try {
        await action();
        if (requestId === loadRequestId) {
          // Move the chart clock past the response so a today-inclusive range can show a bucket
          // newer than the clock captured at request start; the selected range never moves.
          domainNowMs.value = Math.max(domainNowMs.value, Date.now());
        }
      } catch (err: unknown) {
        if (requestId === loadRequestId) {
          errors.value = {
            ...errors.value,
            [key]:
              err instanceof Error
                ? err.message
                : "Could not load library metrics.",
          };
        }
      } finally {
        if (requestId === loadRequestId) {
          loading.value = { ...loading.value, [key]: false };
        }
      }
    }

    return {
      period,
      recentRanges,
      applyPeriod,
      recordRecentRange,
      chartDomain,
      documentReadSeries,
      sectionReadSeries,
      snippetReadSeries,
      leaderboard,
      sectionLeaderboard,
      snippetLeaderboard,
      error,
      errors,
      activeOrganizationId,
      loadingReads,
      loadingLeaderboard,
      loadingSectionLeaderboard,
      loadingSnippetLeaderboard,
      refreshing,
      loadMetrics,
      clearMetrics,
    };
  },
);

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useLibraryMetricsStore, import.meta.hot),
  );
}
