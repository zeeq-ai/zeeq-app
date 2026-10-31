<template>
  <section class="border-b border-default py-3 last:border-b-0">
    <AuditHistoryEventRow
      :entry="parent"
      :expanded="expandedEntryId === parent.id"
      :detail="expandedEntryId === parent.id ? detail : null"
      :detail-loading="expandedEntryId === parent.id && detailLoading"
      :detail-error="expandedEntryId === parent.id ? detailError : null"
      @toggle="emits('toggleDetail', $event)"
      @retry-detail="emits('retryDetail', $event)"
    />

    <div class="ms-11 mt-2 min-w-0">
      <button
        :id="buttonId"
        type="button"
        class="flex w-full items-center justify-between gap-3 rounded-sm text-left text-xs focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
        :aria-expanded="expanded"
        :aria-controls="panelId"
        :aria-label="buttonLabel"
        @click="emits('toggle', operationId)"
      >
        <span class="font-medium text-highlighted">
          Linked deletion changes · {{ visibleChildren.length }} shown
        </span>
        <UIcon
          :name="
            expanded ? 'i-hugeicons-arrow-up-01' : 'i-hugeicons-arrow-down-01'
          "
          class="size-3 shrink-0 text-muted"
          aria-hidden="true"
        />
      </button>

      <div
        v-show="expanded"
        :id="panelId"
        class="mt-2 min-w-0"
        role="region"
        :aria-labelledby="buttonId"
      >
        <div class="flex flex-wrap items-center justify-between gap-2">
          <p class="text-xs text-muted">
            Entries {{ childPage.start }}–{{ childPage.end }} of
            {{ childPage.total }} loaded
          </p>
          <div
            v-if="childPage.pageCount > 1"
            class="flex items-center gap-2"
            aria-label="Loaded deletion changes pages"
          >
            <UButton
              label="Previous"
              icon="i-hugeicons-arrow-left-01"
              color="neutral"
              variant="outline"
              size="xs"
              :disabled="childPage.page === 0"
              @click="showPreviousPage"
            />
            <UButton
              label="Next"
              trailing-icon="i-hugeicons-arrow-right-01"
              color="neutral"
              variant="outline"
              size="xs"
              :disabled="childPage.page >= childPage.pageCount - 1"
              @click="showNextPage"
            />
          </div>
        </div>
        <p class="mt-1 text-xs text-muted">
          These are loaded authorized changes that match the current filters.
          Parent details record the committed totals.
        </p>

        <UAlert
          v-if="operationError"
          class="mt-3"
          title="Could not load deletion changes"
          :description="operationError"
          icon="i-hugeicons-alert-02"
          color="error"
          variant="subtle"
        />

        <div v-if="visibleChildren.length" class="mt-2">
          <AuditHistoryEventRow
            v-for="entry in visibleChildren"
            :key="entry.id"
            :entry
            :library-context-label="libraryContextLabel"
            :expanded="expandedEntryId === entry.id"
            :detail="expandedEntryId === entry.id ? detail : null"
            :detail-loading="expandedEntryId === entry.id && detailLoading"
            :detail-error="expandedEntryId === entry.id ? detailError : null"
            @toggle="emits('toggleDetail', $event)"
            @retry-detail="emits('retryDetail', $event)"
          />
        </div>

        <UButton
          v-if="hasMore"
          :label="
            operationLoading
              ? 'Loading changes'
              : operationError
                ? 'Retry loading changes'
                : 'Load more deletion changes'
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
          No more authorized operation entries were returned. The loaded
          children remain a subset; use the parent details for recorded deletion
          totals.
        </p>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import type {
  AuditHistoryDetail,
  AuditHistoryEntry,
} from "./audit-history-types";
import { getAuditHistoryGroupPage } from "./audit-history-types";
import AuditHistoryEventRow from "./AuditHistoryEventRow.vue";

const props = defineProps<{
  operationId: string;
  parent: AuditHistoryEntry;
  children: AuditHistoryEntry[];
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

const hasMore = computed(
  () => props.operationHasMore || props.operationLoading,
);
const localPage = ref(0);
const childPage = computed(() =>
  getAuditHistoryGroupPage(props.children, localPage.value),
);
const visibleChildren = computed(() => childPage.value.items);
const libraryContextLabel = computed(
  () => `${props.parent.subjectLabel} (${props.parent.subjectId})`,
);
const buttonId = computed(
  () => `audit-library-delete-${props.operationId}-${props.parent.id}`,
);
const panelId = computed(() => `${buttonId.value}-panel`);
const buttonLabel = computed(
  () =>
    `Linked deletion changes, ${visibleChildren.value.length} shown. ${props.expanded ? "Hide" : "Show"} linked changes.`,
);

watch(
  () => `${props.operationId}:${props.parent.id}`,
  () => {
    localPage.value = 0;
  },
);
watch(
  () => props.children.length,
  () => {
    localPage.value = childPage.value.page;
  },
);

function showPreviousPage() {
  localPage.value = Math.max(0, childPage.value.page - 1);
}

function showNextPage() {
  localPage.value = Math.min(
    childPage.value.pageCount - 1,
    childPage.value.page + 1,
  );
}
</script>
