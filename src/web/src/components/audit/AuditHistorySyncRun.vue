<template>
  <section class="border-b border-default py-3 last:border-b-0">
    <button
      :id="buttonId"
      type="button"
      class="flex w-full items-start justify-between gap-3 rounded-sm text-left focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      :aria-label="buttonLabel"
      :aria-expanded="expanded"
      :aria-controls="panelId"
      @click="emits('toggle', operationId)"
    >
      <span class="flex min-w-0 flex-1 items-start gap-3">
        <span
          class="flex size-8 shrink-0 items-center justify-center rounded-full bg-elevated text-muted"
          aria-hidden="true"
        >
          <UIcon name="i-hugeicons-refresh" class="size-4" />
        </span>
        <span class="min-w-0">
          <span class="block break-words text-sm font-medium text-highlighted">
            Sync run · {{ entries.length }} loaded changes
          </span>
          <span class="mt-1 block text-xs text-muted">
            {{ requesterLabel }} {{ firstEntry.actorChannelLabel }}
          </span>
        </span>
      </span>
      <span class="flex shrink-0 flex-col items-end gap-1 text-right">
        <time
          :datetime="firstEntry.occurredAtUtc"
          class="text-xs tabular-nums text-muted"
        >
          {{ timeLabel }}
        </time>
        <UBadge
          label="Run status unknown"
          color="warning"
          variant="subtle"
          size="xs"
        />
      </span>
    </button>

    <div v-show="expanded" :id="panelId" class="mt-2 pl-0 sm:pl-11">
      <p class="break-all font-mono text-xs text-muted">
        Operation ID · {{ operationId }}
      </p>
      <p class="mt-1 text-xs text-muted">
        This group shows loaded changes only. The run may have more entries.
      </p>
      <UAlert
        v-if="operationError"
        title="Could not load more run entries"
        :description="operationError"
        icon="i-hugeicons-alert-02"
        color="error"
        variant="subtle"
        class="mt-3"
      />
      <div class="mt-2">
        <AuditHistoryEventRow
          v-for="entry in entries"
          :key="entry.id"
          :entry
          :expanded="expandedEntryId === entry.id"
          :detail="expandedEntryId === entry.id ? detail : null"
          :detail-loading="expandedEntryId === entry.id && detailLoading"
          :detail-error="expandedEntryId === entry.id ? detailError : null"
          @toggle="emits('toggleDetail', $event)"
          @retry-detail="emits('retryDetail', $event)"
        />
      </div>
      <UButton
        v-if="hasMoreForOperation"
        :label="
          operationLoading
            ? 'Loading changes'
            : operationError
              ? 'Retry loading changes'
              : 'Load more changes for this run'
        "
        icon="i-hugeicons-more-horizontal"
        color="neutral"
        variant="outline"
        size="sm"
        class="mt-2"
        :loading="operationLoading"
        :disabled="operationLoading"
        @click="emits('loadOperation', operationId)"
      />
      <p v-else class="mt-2 text-xs text-muted">
        No more entries were returned. Run status is not recorded.
      </p>
    </div>
  </section>
</template>

<script setup lang="ts">
import type {
  AuditHistoryDetail,
  AuditHistoryEntry,
} from "./audit-history-types";
import AuditHistoryEventRow from "./AuditHistoryEventRow.vue";
import { formatAuditTime } from "./audit-history-types";

const props = defineProps<{
  operationId: string;
  entries: AuditHistoryEntry[];
  expanded: boolean;
  expandedEntryId: string | null;
  detail: AuditHistoryDetail | null;
  detailLoading: boolean;
  detailError: string | null;
  operationHasMore: boolean;
  operationLoading: boolean;
  operationError: string | null;
}>();

const emits = defineEmits<{
  toggle: [operationId: string];
  toggleDetail: [entryId: string];
  retryDetail: [entryId: string];
  loadOperation: [operationId: string];
}>();

const firstEntry = computed(() => props.entries[0]!);
const timeLabel = computed(() =>
  formatAuditTime(firstEntry.value.occurredAtUtc),
);
const requesterLabel = computed(() =>
  ["user", "user_token", "system_admin"].includes(firstEntry.value.actorKind)
    ? `Requested by ${firstEntry.value.actorLabel}`
    : firstEntry.value.actorLabel,
);
const buttonId = computed(
  () => `audit-sync-${props.operationId}-${firstEntry.value.id}`,
);
const panelId = computed(
  () => `audit-sync-panel-${props.operationId}-${firstEntry.value.id}`,
);
const buttonLabel = computed(
  () =>
    `Sync run, ${props.entries.length} loaded changes. ${props.expanded ? "Hide" : "Show"} run entries.`,
);
const hasMoreForOperation = computed(
  () => props.operationHasMore || props.operationLoading,
);
</script>
