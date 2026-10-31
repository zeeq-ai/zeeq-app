<template>
  <USlideover
    v-model:open="open"
    :title="title"
    :description="description"
    side="right"
    :ui="{ content: 'max-w-2xl', body: 'p-0 sm:p-0' }"
    @after:leave="emits('close')"
  >
    <template #body>
      <div class="flex h-full min-h-0 flex-col">
        <AuditHistoryFilters
          :scope
          :scope-label
          :show-scope-badge="showScopeBadge"
          :range
          :entries
          :total-count
          :type-filter
          :area-filter
          :actor-filter
          :actor-filter-label
          @type-filter-change="emits('typeFilterChange', $event)"
          @area-filter-change="emits('areaFilterChange', $event)"
          @actor-filter-change="
            (id, label) => emits('actorFilterChange', id, label)
          "
          @range-change="emits('rangeChange', $event)"
          @clear-filters="emits('clearFilters')"
        />

        <div v-if="captureNote" class="mt-4 px-5 sm:px-6">
          <UAlert
            title="History coverage"
            :description="formatAuditCoverage(captureNote.description)"
            icon="i-hugeicons-information-circle"
            color="neutral"
            variant="subtle"
          />
        </div>

        <div
          v-if="loading && entries.length === 0"
          class="grid gap-3 px-6 py-4"
          role="status"
          aria-live="polite"
        >
          <USkeleton v-for="index in 5" :key="index" class="h-20 rounded-md" />
          <span class="sr-only">Loading audit history</span>
        </div>

        <UAlert
          v-else-if="error && entries.length === 0"
          title="Could not load history"
          :description="error"
          icon="i-hugeicons-alert-02"
          color="error"
          variant="subtle"
          class="m-5"
        >
          <template #actions>
            <UButton
              label="Retry"
              color="neutral"
              variant="outline"
              size="sm"
              @click="emits('retry')"
            />
          </template>
        </UAlert>

        <div
          v-else-if="filteredEntries.length === 0"
          class="flex flex-1 flex-col items-center justify-center px-6 py-12 text-center"
        >
          <UEmpty
            icon="i-hugeicons-time-02"
            title="No history entries"
            :description="emptyDescription"
            variant="naked"
          />
          <UButton
            v-if="range === '90-days'"
            class="mt-4"
            label="Widen to all time"
            icon="i-hugeicons-calendar-03"
            color="neutral"
            variant="outline"
            @click="emits('searchAllTime')"
          />
        </div>

        <div v-else class="min-h-0 flex-1 overflow-y-auto px-5 sm:px-6">
          <UTimeline
            v-if="scope === 'document'"
            :items="timelineItems"
            size="sm"
            :ui="{
              root: 'gap-0',
              item: 'flex-none gap-3',
              indicator: 'bg-elevated text-muted',
              wrapper: 'min-w-0 mt-0 pb-5',
              title: 'hidden',
              date: 'hidden',
            }"
            class="mt-3"
          >
            <template #history-indicator="{ item }">
              <UIcon
                :name="getTimelineIcon(item)"
                class="size-4"
                aria-hidden="true"
              />
            </template>
            <template #history-wrapper="{ item }">
              <AuditHistoryEventRow
                :entry="getTimelineEntry(item)"
                timeline
                show-date
                :expanded="expandedEntryId === getTimelineEntry(item).id"
                :detail="
                  expandedEntryId === getTimelineEntry(item).id ? detail : null
                "
                :detail-loading="
                  expandedEntryId === getTimelineEntry(item).id && detailLoading
                "
                :detail-error="
                  expandedEntryId === getTimelineEntry(item).id
                    ? detailError
                    : null
                "
                @toggle="emits('toggleDetail', $event)"
                @retry-detail="emits('retryDetail', $event)"
              />
            </template>
          </UTimeline>

          <div v-else class="mt-2">
            <section
              v-for="group in dayGroups"
              :key="group.key"
              :aria-label="group.label"
            >
              <h3
                class="sticky top-0 z-10 -mx-5 border-b border-default bg-default/95 px-5 py-2 text-xs font-medium text-muted backdrop-blur sm:-mx-6 sm:px-6"
              >
                {{ group.label }}
              </h3>
              <template v-for="item in group.items" :key="item.key">
                <AuditHistoryEventRow
                  v-if="item.kind === 'event'"
                  :entry="item.entry"
                  :expanded="expandedEntryId === item.entry.id"
                  :detail="expandedEntryId === item.entry.id ? detail : null"
                  :detail-loading="
                    expandedEntryId === item.entry.id && detailLoading
                  "
                  :detail-error="
                    expandedEntryId === item.entry.id ? detailError : null
                  "
                  @toggle="emits('toggleDetail', $event)"
                  @retry-detail="emits('retryDetail', $event)"
                />
                <AuditHistorySyncRun
                  v-else-if="item.kind === 'sync-run'"
                  :operation-id="item.operationId"
                  :entries="item.entries"
                  :expanded="expandedOperationId === item.operationId"
                  :expanded-entry-id="expandedEntryId"
                  :detail
                  :detail-loading
                  :detail-error
                  :operation-has-more="item.operationState.hasMore"
                  :operation-loading="item.operationState.loading"
                  :operation-error="item.operationState.error ?? null"
                  @toggle="emits('toggleOperation', $event)"
                  @toggle-detail="emits('toggleDetail', $event)"
                  @retry-detail="emits('retryDetail', $event)"
                  @load-operation="emits('loadOperation', $event)"
                />
                <AuditHistoryLibraryDeletion
                  v-else
                  :operation-id="item.operationId"
                  :parent="item.parent"
                  :children="item.children"
                  :expanded="expandedOperationId === item.operationId"
                  :expanded-entry-id="expandedEntryId"
                  :detail
                  :detail-loading
                  :detail-error
                  :operation-has-more="item.operationState.hasMore"
                  :operation-loading="item.operationState.loading"
                  :operation-error="item.operationState.error ?? null"
                  @toggle="emits('toggleOperation', $event)"
                  @toggle-detail="emits('toggleDetail', $event)"
                  @retry-detail="emits('retryDetail', $event)"
                  @load-operation="emits('loadOperation', $event)"
                />
              </template>
            </section>
          </div>

          <UAlert
            v-if="error && entries.length > 0"
            title="Could not load more history"
            :description="error"
            icon="i-hugeicons-alert-02"
            color="error"
            variant="subtle"
            class="my-4"
          >
            <template #actions>
              <UButton
                label="Retry"
                color="neutral"
                variant="outline"
                size="sm"
                @click="emits('retry')"
              />
            </template>
          </UAlert>
        </div>

        <footer
          v-if="entries.length > 0"
          class="flex items-center justify-between gap-3 border-t border-default px-5 py-3 sm:px-6"
        >
          <p class="text-xs text-muted">
            {{ rangeStatusLabel }}
          </p>
          <UButton
            v-if="hasMore"
            label="Load older entries"
            trailing-icon="i-hugeicons-arrow-down-01"
            color="neutral"
            variant="outline"
            size="sm"
            :loading="loading"
            :disabled="loading"
            @click="emits('loadOlder')"
          />
          <UButton
            v-else-if="range === '90-days'"
            label="Search all time"
            trailing-icon="i-hugeicons-arrow-right-01"
            color="neutral"
            variant="outline"
            size="sm"
            @click="emits('searchAllTime')"
          />
        </footer>
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import type { TimelineItem } from "@nuxt/ui";
import type {
  AuditHistoryDetail,
  AuditHistoryArea,
  AuditHistoryEntry,
  AuditHistoryEntryType,
  AuditHistoryOperationState,
  AuditHistoryRange,
  AuditHistoryScope,
} from "./audit-history-types";
import AuditHistoryEventRow from "./AuditHistoryEventRow.vue";
import AuditHistoryFilters from "./AuditHistoryFilters.vue";
import AuditHistoryLibraryDeletion from "./AuditHistoryLibraryDeletion.vue";
import AuditHistorySyncRun from "./AuditHistorySyncRun.vue";
import {
  formatAuditCoverage,
  groupAuditFeedEntriesByUtcDay,
} from "./audit-history-types";

type AuditTimelineItem = TimelineItem & { entry: AuditHistoryEntry };

const open = defineModel<boolean>("open", { required: true });

const props = defineProps<{
  scope: AuditHistoryScope;
  scopeLabel: string;
  showScopeBadge?: boolean;
  title?: string;
  description?: string;
  range: AuditHistoryRange;
  entries: AuditHistoryEntry[];
  totalCount: number | null;
  hasMore: boolean;
  loading: boolean;
  error: string | null;
  typeFilter: AuditHistoryEntryType | "all";
  areaFilter: AuditHistoryArea | "all";
  actorFilter: string | null;
  actorFilterLabel: string | null;
  coverageLabel: string;
  captureNote: { title: string; description: string } | null;
  expandedEntryId: string | null;
  detail: AuditHistoryDetail | null;
  detailLoading: boolean;
  detailError: string | null;
  expandedOperationId: string | null;
  operationStates: Record<string, AuditHistoryOperationState>;
}>();

const emits = defineEmits<{
  close: [];
  retry: [];
  retryDetail: [entryId: string];
  toggleDetail: [entryId: string];
  toggleOperation: [operationId: string];
  loadOperation: [operationId: string];
  typeFilterChange: [type: AuditHistoryEntryType | "all"];
  areaFilterChange: [area: AuditHistoryArea | "all"];
  actorFilterChange: [actorId: string | null, actorLabel: string | null];
  rangeChange: [range: AuditHistoryRange];
  clearFilters: [];
  loadOlder: [];
  searchAllTime: [];
}>();

const title = computed(() => props.title ?? "History");
const showScopeBadge = computed(() => props.showScopeBadge ?? true);
const description = computed(
  () => props.description ?? "Recent changes, newest first.",
);
const filteredEntries = computed(() =>
  props.typeFilter === "all"
    ? props.entries
    : props.entries.filter((entry) => entry.type === props.typeFilter),
);
const dayGroups = computed(() =>
  groupAuditFeedEntriesByUtcDay(filteredEntries.value, props.operationStates),
);
const timelineItems = computed<AuditTimelineItem[]>(() =>
  filteredEntries.value.map((entry) => ({
    slot: "history",
    title: entry.actionLabel,
    icon: getEntryIcon(entry),
    entry,
  })),
);
function getEntryIcon(entry: AuditHistoryEntry): string {
  switch (entry.type) {
    case "added":
      return "i-hugeicons-file-add";
    case "deleted":
      return "i-hugeicons-file-remove";
    default:
      return "i-hugeicons-file-edit";
  }
}
function getTimelineEntry(item: unknown): AuditHistoryEntry {
  return (item as AuditTimelineItem).entry;
}
function getTimelineIcon(item: unknown): string {
  return (item as AuditTimelineItem).icon;
}
const emptyDescription = computed(() =>
  props.typeFilter === "all"
    ? "No captured history entries were found for this range."
    : "No entries match this type in the selected range.",
);
const rangeStatusLabel = computed(() => {
  if (props.hasMore) return "More entries are available.";
  return props.range === "90-days"
    ? "No more entries in the last 90 days."
    : "All loaded entries are shown.";
});
</script>
