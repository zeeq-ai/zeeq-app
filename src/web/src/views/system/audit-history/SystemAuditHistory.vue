<template>
  <AuditHistoryFeed
    v-model:open="open"
    scope="system"
    scope-label="System"
    title="System history"
    description="Changes made in the system console and system-owned records."
    :range="range"
    :entries="entries"
    :total-count="null"
    :has-more="hasMore"
    :loading="loading"
    :error="error"
    :type-filter="typeFilter"
    :area-filter="areaFilter"
    :actor-filter="actorFilter"
    :actor-filter-label="actorFilterLabel"
    :coverage-label="coverage"
    :capture-note="captureNote"
    :expanded-entry-id="expandedEntryId"
    :detail="detail"
    :detail-loading="detailLoading"
    :detail-error="detailError"
    :expanded-operation-id="expandedOperationId"
    :operation-states="operationStates"
    @close="closeHistory"
    @retry="historyStore.retry"
    @retry-detail="historyStore.retryDetail"
    @toggle-detail="historyStore.toggleDetail"
    @toggle-operation="historyStore.toggleOperation"
    @load-operation="historyStore.loadOperation"
    @type-filter-change="historyStore.setTypeFilter"
    @area-filter-change="historyStore.setAreaFilter"
    @actor-filter-change="historyStore.setActorFilter"
    @range-change="historyStore.setRange"
    @load-older="historyStore.loadOlder"
    @search-all-time="historyStore.searchAllTime"
  />
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import AuditHistoryFeed from "@/components/audit/AuditHistoryFeed.vue";
import { useAuditHistoryStore } from "@/stores/audit-history-store";
import { useRouter } from "vue-router";

const open = ref(true);
const router = useRouter();
const historyStore = useAuditHistoryStore();
const {
  range,
  entries,
  hasMore,
  loading,
  error,
  typeFilter,
  areaFilter,
  actorFilter,
  actorFilterLabel,
  coverage,
  expandedEntryId,
  detail,
  detailLoading,
  detailError,
  expandedOperationId,
  operationStates,
} = storeToRefs(historyStore);
const captureNote = computed(() =>
  coverage.value
    ? {
        title: "Capture coverage",
        description: coverage.value,
      }
    : null,
);

historyStore.setContext({ scope: "system" });
void historyStore.loadInitial();

function closeHistory() {
  void router.replace("/system/diagnostics");
}
</script>
