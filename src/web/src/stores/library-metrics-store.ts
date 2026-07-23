import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Metrics,
  metricSeriesGroupEnum,
  type MetricLeaderboardItem,
  type MetricSeriesPoint,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import {
  counterMetricType,
  sharedMetricWindow,
} from "@/stores/metrics-store";

/**
 * Metrics state for the Libraries root panel.
 *
 * Kept separate from the Home dashboard metrics store so the Libraries route
 * has its own window and library scope without mutating Home's filters/data.
 */
export const useLibraryMetricsStore = defineStore(
  "library-metrics-store",
  () => {
    const appStore = useAppStore();

    const window = sharedMetricWindow;
    const documentReadSeries = ref<MetricSeriesPoint[]>([]);
    const sectionReadSeries = ref<MetricSeriesPoint[]>([]);
    const snippetReadSeries = ref<MetricSeriesPoint[]>([]);
    const leaderboard = ref<MetricLeaderboardItem[]>([]);
    const sectionLeaderboard = ref<MetricLeaderboardItem[]>([]);
    const snippetLeaderboard = ref<MetricLeaderboardItem[]>([]);
    const loading = ref<Record<string, boolean>>({});
    const error = ref<string | null>(null);

    const activeOrganizationId = computed(
      () =>
        appStore.currentOrganization?.id ?? appStore.user?.organizationId ?? "",
    );
    const loadingReads = computed(
      () =>
        loading.value.documentReads ||
        loading.value.sectionReads ||
        loading.value.snippetReads ||
        false,
    );
    const loadingLeaderboard = computed(
      () => loading.value.leaderboard ?? false,
    );
    const loadingSectionLeaderboard = computed(
      () => loading.value.sectionLeaderboard ?? false,
    );
    const loadingSnippetLeaderboard = computed(
      () => loading.value.snippetLeaderboard ?? false,
    );
    const refreshing = computed(() =>
      Object.values(loading.value).some(Boolean),
    );

    let loadRequestId = 0;

    async function loadMetrics(libraryName: string | null) {
      const requestId = ++loadRequestId;
      const orgId = activeOrganizationId.value;
      if (!orgId || !libraryName) {
        clearMetrics();
        return;
      }

      resetMetricData();
      await Promise.all([
        loadSeries(
          "documentReads",
          counterMetricType.documentRead,
          orgId,
          libraryName,
          requestId,
        ),
        loadSeries(
          "sectionReads",
          counterMetricType.sectionRead,
          orgId,
          libraryName,
          requestId,
        ),
        loadSeries(
          "snippetReads",
          counterMetricType.snippetRead,
          orgId,
          libraryName,
          requestId,
        ),
        loadLeaderboard(orgId, libraryName, requestId),
        loadSectionLeaderboard(
          "sectionLeaderboard",
          "section",
          orgId,
          libraryName,
          requestId,
        ),
        loadSectionLeaderboard(
          "snippetLeaderboard",
          "code",
          orgId,
          libraryName,
          requestId,
        ),
      ]);
    }

    function clearMetrics() {
      resetMetricData();
      loadRequestId++;
    }

    function resetMetricData() {
      documentReadSeries.value = [];
      sectionReadSeries.value = [];
      snippetReadSeries.value = [];
      leaderboard.value = [];
      sectionLeaderboard.value = [];
      snippetLeaderboard.value = [];
      loading.value = {};
      error.value = null;
    }

    async function loadSeries(
      key: "documentReads" | "sectionReads" | "snippetReads",
      metricType: string,
      orgId: string,
      libraryName: string,
      requestId: number,
    ) {
      await run(key, requestId, async () => {
        const series = await Metrics.getMetricSeries(orgId, metricType, {
          window: window.value,
          groupBy: metricSeriesGroupEnum.None,
          libraries: [libraryName],
        });

        if (requestId !== loadRequestId) return;
        if (key === "documentReads") documentReadSeries.value = series;
        if (key === "sectionReads") sectionReadSeries.value = series;
        if (key === "snippetReads") snippetReadSeries.value = series;
      });
    }

    async function loadLeaderboard(
      orgId: string,
      libraryName: string,
      requestId: number,
    ) {
      await run("leaderboard", requestId, async () => {
        const items = await Metrics.getMetricLeaderboard(orgId, {
          window: window.value,
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
      requestId: number,
    ) {
      await run(key, requestId, async () => {
        const items = await Metrics.getMetricSectionLeaderboard(orgId, {
          window: window.value,
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

    async function run(
      key: string,
      requestId: number,
      action: () => Promise<void>,
    ) {
      loading.value = { ...loading.value, [key]: true };
      try {
        await action();
      } catch (err: unknown) {
        if (requestId === loadRequestId) {
          error.value =
            err instanceof Error
              ? err.message
              : "Could not load library metrics.";
        }
      } finally {
        if (requestId === loadRequestId) {
          loading.value = { ...loading.value, [key]: false };
        }
      }
    }

    return {
      window,
      documentReadSeries,
      sectionReadSeries,
      snippetReadSeries,
      leaderboard,
      sectionLeaderboard,
      snippetLeaderboard,
      error,
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
