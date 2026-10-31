import { defineStore, acceptHMRUpdate } from "pinia";
import { AuditHistory } from "@/api/generated";
import {
  mapAuditEventDetail,
  mapAuditEventSummary,
} from "@/components/audit/audit-history-mappers";
import type {
  AuditHistoryArea,
  AuditHistoryDetail,
  AuditHistoryEntry,
  AuditHistoryEntryType,
  AuditHistoryOperationState,
  AuditHistoryRange,
} from "@/components/audit/audit-history-types";
import {
  buildAuditHistoryQuery,
  hasAuditHistoryContextChanged,
  isSystemAuditContext,
  type AuditHistoryContextIdentity,
} from "./audit-history-query";

export type AuditHistoryContext = AuditHistoryContextIdentity & {
  libraryLabels?: Record<string, string>;
};

type OperationPageState = AuditHistoryOperationState & {
  cursor: string | null;
};

/** Owns audit list/detail requests for the active root view. Child components receive props only. */
export const useAuditHistoryStore = defineStore("audit-history-store", () => {
  const context = ref<AuditHistoryContext | null>(null);
  const range = ref<AuditHistoryRange>("90-days");
  const entries = ref<AuditHistoryEntry[]>([]);
  const nextCursor = ref<string | null>(null);
  const coverage = ref("");
  const loading = ref(false);
  const error = ref<string | null>(null);
  const typeFilter = ref<AuditHistoryEntryType | "all">("all");
  const areaFilter = ref<AuditHistoryArea | "all">("all");
  const actorFilter = ref<string | null>(null);
  const actorFilterLabel = ref<string | null>(null);
  const expandedEntryId = ref<string | null>(null);
  const detail = ref<AuditHistoryDetail | null>(null);
  const detailLoading = ref(false);
  const detailError = ref<string | null>(null);
  const expandedOperationId = ref<string | null>(null);
  const operationStates = reactive<Record<string, OperationPageState>>({});

  let feedRequestId = 0;
  let detailRequestId = 0;
  let nextOperationRequestId = 0;
  const operationRequestIds = new Map<string, number>();

  const hasMore = computed(() => nextCursor.value !== null);

  function setContext(next: AuditHistoryContext) {
    const unchanged = !hasAuditHistoryContextChanged(context.value, next);
    context.value = { ...next };

    if (unchanged) {
      for (const entry of entries.value) {
        entry.libraryLabel = resolveLibraryLabel(entry.libraryId, next);
      }
    }

    if (!unchanged) {
      range.value = "90-days";
      typeFilter.value = "all";
      areaFilter.value = "all";
      actorFilter.value = null;
      actorFilterLabel.value = null;
      clearFeed();
    }
  }

  async function loadInitial() {
    clearFeed();
    return loadPage(false);
  }

  async function retry() {
    return nextCursor.value ? loadPage(true) : loadPage(false);
  }

  async function loadOlder() {
    if (!nextCursor.value || loading.value) return;
    return loadPage(true);
  }

  async function searchAllTime() {
    range.value = "all-time";
    clearFeed();
    return loadPage(false);
  }

  async function setRange(next: AuditHistoryRange) {
    if (range.value === next) return;
    range.value = next;
    clearFeed();
    return loadPage(false);
  }

  async function setTypeFilter(next: AuditHistoryEntryType | "all") {
    if (typeFilter.value === next) return;
    typeFilter.value = next;
    clearFeed();
    return loadPage(false);
  }

  async function setAreaFilter(next: AuditHistoryArea | "all") {
    if (areaFilter.value === next) return;
    areaFilter.value = next;
    clearFeed();
    return loadPage(false);
  }

  async function setActorFilter(next: string | null, label: string | null) {
    if (actorFilter.value === next) return;
    actorFilter.value = next;
    actorFilterLabel.value = next ? label : null;
    clearFeed();
    return loadPage(false);
  }

  async function toggleDetail(entryId: string) {
    if (expandedEntryId.value === entryId) {
      expandedEntryId.value = null;
      detail.value = null;
      detailError.value = null;
      detailRequestId++;
      detailLoading.value = false;
      return;
    }

    expandedEntryId.value = entryId;
    detail.value = null;
    detailError.value = null;
    await loadDetail(entryId);
  }

  async function retryDetail(entryId: string) {
    if (expandedEntryId.value === entryId) return loadDetail(entryId);
  }

  async function toggleOperation(operationId: string) {
    expandedOperationId.value =
      expandedOperationId.value === operationId ? null : operationId;
  }

  async function loadOperation(operationId: string) {
    const state = (operationStates[operationId] ??= {
      hasMore: true,
      loading: false,
      cursor: null,
      error: null,
    });
    if (state.loading || (!state.hasMore && state.cursor === null)) return;

    const requestId = ++nextOperationRequestId;
    operationRequestIds.set(operationId, requestId);
    state.loading = true;

    try {
      const response = await listHistory({
        cursor: state.cursor ?? undefined,
        operationId,
      });
      if (operationRequestIds.get(operationId) !== requestId) return;

      state.error = null;
      entries.value = mergeEntries(
        entries.value,
        response.entries.map((event) => mapSummary(event)),
      );
      state.cursor = response.nextCursor ?? null;
      state.hasMore = state.cursor !== null;
    } catch (err: unknown) {
      // Keep the loaded group visible and allow the same page request to retry.
      if (operationRequestIds.get(operationId) === requestId) {
        state.error = toErrorMessage(err, "Could not load more run entries.");
        state.hasMore = true;
      }
    } finally {
      if (operationRequestIds.get(operationId) === requestId) {
        state.loading = false;
      }
    }
  }

  async function loadPage(append: boolean) {
    if (!context.value || loading.value) return;

    const requestId = ++feedRequestId;
    loading.value = true;
    error.value = null;

    try {
      const response = await listHistory({
        cursor: append ? (nextCursor.value ?? undefined) : undefined,
      });
      if (feedRequestId !== requestId) return;

      const mapped = response.entries.map((event) => mapSummary(event));
      entries.value = append ? mergeEntries(entries.value, mapped) : mapped;
      nextCursor.value = response.nextCursor ?? null;
      coverage.value = response.coverage;
    } catch (err: unknown) {
      if (feedRequestId === requestId) {
        error.value = toErrorMessage(err, "Could not load history.");
      }
    } finally {
      if (feedRequestId === requestId) loading.value = false;
    }
  }

  async function loadDetail(entryId: string) {
    const event = entries.value.find((item) => item.id === entryId);
    const activeContext = context.value;
    if (!event || !activeContext) return;

    const requestId = ++detailRequestId;
    detailLoading.value = true;
    detailError.value = null;

    try {
      const params = { occurredAtUtc: event.occurredAtUtc };
      const response = isSystemAuditContext(activeContext)
        ? await AuditHistory.getSystemAuditHistoryEvent(event.id, params)
        : await AuditHistory.getAuditHistoryEvent(event.id, params);
      if (detailRequestId !== requestId || expandedEntryId.value !== entryId) {
        return;
      }
      detail.value = mapAuditEventDetail(response);
    } catch (err: unknown) {
      if (detailRequestId === requestId) {
        detailError.value = toErrorMessage(
          err,
          "Could not load event details.",
        );
      }
    } finally {
      if (detailRequestId === requestId) detailLoading.value = false;
    }
  }

  function listHistory(overrides: { cursor?: string; operationId?: string }) {
    const activeContext = context.value;
    if (!activeContext) throw new Error("History scope is not selected.");

    const params = buildAuditHistoryQuery(
      activeContext,
      range.value,
      {
        type: typeFilter.value,
        area: areaFilter.value,
        actorId: actorFilter.value,
      },
      overrides,
    );

    return isSystemAuditContext(activeContext)
      ? AuditHistory.listSystemAuditHistory(params)
      : AuditHistory.listAuditHistory(params);
  }

  function mapSummary(event: Parameters<typeof mapAuditEventSummary>[0]) {
    const entry = mapAuditEventSummary(event);
    return {
      ...entry,
      libraryId: event.libraryId ?? null,
      libraryLabel: resolveLibraryLabel(event.libraryId, context.value),
    };
  }

  function resolveLibraryLabel(
    libraryId: string | null | undefined,
    activeContext: AuditHistoryContext | null | undefined,
  ) {
    if (!libraryId) return null;
    return activeContext?.libraryLabels?.[libraryId] ?? `Library ${libraryId}`;
  }

  function clearFeed() {
    feedRequestId++;
    detailRequestId++;
    operationRequestIds.clear();
    entries.value = [];
    nextCursor.value = null;
    coverage.value = "";
    loading.value = false;
    error.value = null;
    expandedEntryId.value = null;
    detail.value = null;
    detailLoading.value = false;
    detailError.value = null;
    expandedOperationId.value = null;
    for (const key of Object.keys(operationStates)) delete operationStates[key];
  }

  return {
    context,
    range,
    entries,
    nextCursor,
    hasMore,
    coverage,
    loading,
    error,
    typeFilter,
    areaFilter,
    actorFilter,
    actorFilterLabel,
    expandedEntryId,
    detail,
    detailLoading,
    detailError,
    expandedOperationId,
    operationStates,
    setContext,
    loadInitial,
    retry,
    loadOlder,
    searchAllTime,
    setRange,
    setTypeFilter,
    setAreaFilter,
    setActorFilter,
    toggleDetail,
    retryDetail,
    toggleOperation,
    loadOperation,
  };
});

function mergeEntries(
  current: AuditHistoryEntry[],
  next: AuditHistoryEntry[],
): AuditHistoryEntry[] {
  const seen = new Set(current.map((entry) => entry.id));
  return [...current, ...next.filter((entry) => !seen.has(entry.id))].sort(
    compareAuditEntriesDescending,
  );
}

function compareAuditEntriesDescending(
  left: AuditHistoryEntry,
  right: AuditHistoryEntry,
): number {
  const timestampOrder = compareAuditTimestampsDescending(
    left.occurredAtUtc,
    right.occurredAtUtc,
  );
  if (timestampOrder !== 0) return timestampOrder;

  const leftId = left.id.toLowerCase();
  const rightId = right.id.toLowerCase();
  return leftId === rightId ? 0 : leftId > rightId ? -1 : 1;
}

// Match the server's timestamp/UUID keyset order. Parse whole seconds for timezone handling,
// then compare fractional digits separately so sub-millisecond events do not collapse into ties.
function compareAuditTimestampsDescending(left: string, right: string): number {
  const leftWholeSecond = Date.parse(
    left.replace(/\.\d+(?=(?:Z|[+-]\d{2}:?\d{2})?$)/i, ""),
  );
  const rightWholeSecond = Date.parse(
    right.replace(/\.\d+(?=(?:Z|[+-]\d{2}:?\d{2})?$)/i, ""),
  );

  if (
    Number.isFinite(leftWholeSecond) &&
    Number.isFinite(rightWholeSecond) &&
    leftWholeSecond !== rightWholeSecond
  ) {
    return rightWholeSecond - leftWholeSecond;
  }

  if (leftWholeSecond !== rightWholeSecond) {
    return left === right ? 0 : left > right ? -1 : 1;
  }

  const leftFraction = timestampFraction(left);
  const rightFraction = timestampFraction(right);
  const fractionLength = Math.max(leftFraction.length, rightFraction.length);
  const normalizedLeft = leftFraction.padEnd(fractionLength, "0");
  const normalizedRight = rightFraction.padEnd(fractionLength, "0");

  return normalizedLeft === normalizedRight
    ? 0
    : normalizedLeft > normalizedRight
      ? -1
      : 1;
}

function timestampFraction(value: string): string {
  return value.match(/\.(\d+)(?=(?:Z|[+-]\d{2}:?\d{2})?$)/i)?.[1] ?? "";
}

function toErrorMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useAuditHistoryStore, import.meta.hot),
  );
}
