<template>
  <article
    class="min-w-0"
    :class="
      timeline
        ? 'border-b-0 pt-1 pb-0'
        : 'border-b border-default py-3 last:border-b-0'
    "
  >
    <button
      :id="`audit-entry-${entry.id}`"
      type="button"
      class="group flex w-full min-w-0 items-start justify-between gap-3 rounded-sm text-left focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      :aria-label="rowAriaLabel"
      :aria-expanded="expanded"
      :aria-controls="detailPanelId"
      @click="emits('toggle', entry.id)"
    >
      <span class="flex min-w-0 flex-1 items-start gap-3">
        <span
          v-if="!timeline"
          class="flex size-8 shrink-0 items-center justify-center rounded-full bg-elevated text-muted"
          aria-hidden="true"
        >
          <UIcon :name="icon" class="size-4" />
        </span>
        <span class="min-w-0 flex-1">
          <span
            class="block break-words text-sm text-highlighted group-hover:text-primary"
          >
            <span class="font-medium">{{ entry.actorLabel }}</span>
            {{ entry.actionLabel }}
            <span class="font-medium">{{ entry.subjectLabel }}</span>
            <span
              v-if="entry.subjectPath"
              class="block break-all font-mono text-xs text-muted"
            >
              {{ entry.subjectPath }}
            </span>
          </span>
          <span
            class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted"
          >
            <span>{{ entry.actorChannelLabel }}</span>
            <span aria-hidden="true">·</span>
            <span>{{ entry.areaLabel }}</span>
            <UBadge
              v-if="timeline"
              :label="typeLabel"
              :color="typeColor"
              variant="subtle"
              size="xs"
            />
            <UBadge
              v-if="timeline && timelineSourceLabel"
              :label="timelineSourceLabel"
              color="neutral"
              variant="subtle"
              size="xs"
            />
            <UBadge
              v-if="entry.actorIsSystemAdmin"
              label="System admin"
              color="warning"
              variant="subtle"
              size="xs"
            />
            <UBadge
              v-if="entry.partial"
              label="Partial"
              color="warning"
              variant="subtle"
              size="xs"
            />
          </span>
          <span
            v-if="entry.organizationLabel"
            class="mt-1 block text-xs text-muted"
          >
            Organization · {{ entry.organizationLabel }}
          </span>
          <span
            v-if="entry.libraryLabel && !timeline"
            class="mt-1 block break-words text-xs text-muted"
          >
            Library (current name) · {{ entry.libraryLabel }}
          </span>
        </span>
      </span>

      <span
        class="flex shrink-0 text-right"
        :class="
          timeline ? 'flex-row items-center gap-1' : 'flex-col items-end gap-1'
        "
      >
        <time
          :datetime="entry.occurredAtUtc"
          :aria-label="fullDateTimeLabel"
          class="text-xs tabular-nums text-muted"
        >
          {{ timeline ? timelineDateTimeLabel : timeLabel }}
        </time>
        <UBadge
          v-if="!timeline"
          :label="typeLabel"
          :color="typeColor"
          variant="subtle"
          size="xs"
        />
        <UBadge
          v-if="!timeline"
          :label="entry.sourceLabel"
          color="neutral"
          variant="subtle"
          size="xs"
          class="max-w-28 truncate"
          :title="entry.sourceLabel"
        />
        <UIcon
          :name="
            expanded ? 'i-hugeicons-arrow-up-01' : 'i-hugeicons-arrow-down-01'
          "
          class="size-3 text-muted"
          aria-hidden="true"
        />
      </span>
    </button>

    <div
      v-show="expanded"
      :id="detailPanelId"
      class="min-w-0"
      :class="timeline ? '' : 'ms-11'"
      role="region"
      :aria-labelledby="detailHeadingId"
    >
      <h3 :id="detailHeadingId" class="sr-only">
        Details for {{ entry.actorLabel }} {{ entry.actionLabel }}
        {{ entry.subjectLabel }}
      </h3>
      <div
        v-if="detailLoading"
        class="space-y-2 py-3"
        role="status"
        aria-live="polite"
      >
        <USkeleton class="h-4 w-2/3" />
        <USkeleton class="h-16 w-full" />
        <span class="sr-only">Loading event details</span>
      </div>
      <UAlert
        v-else-if="detailError"
        title="Could not load details"
        :description="detailError"
        icon="i-hugeicons-alert-02"
        color="error"
        variant="subtle"
        class="mt-3"
      >
        <template #actions>
          <UButton
            label="Retry"
            color="neutral"
            variant="outline"
            size="xs"
            @click="emits('retryDetail', entry.id)"
          />
        </template>
      </UAlert>
      <AuditHistoryDetailView v-else-if="detail" :detail />
      <p v-else class="mt-3 text-xs text-muted">
        No additional details were recorded for this entry.
      </p>
    </div>
  </article>
</template>

<script setup lang="ts">
import type {
  AuditHistoryDetail,
  AuditHistoryEntry,
} from "./audit-history-types";
import AuditHistoryDetailView from "./AuditHistoryDetail.vue";
import {
  formatAuditDateTime,
  formatAuditTime,
  formatAuditTimelineDateTime,
} from "./audit-history-types";

const props = defineProps<{
  entry: AuditHistoryEntry;
  expanded: boolean;
  detail: AuditHistoryDetail | null;
  detailLoading: boolean;
  detailError: string | null;
  showDate?: boolean;
  timeline?: boolean;
}>();

const emits = defineEmits<{
  toggle: [entryId: string];
  retryDetail: [entryId: string];
}>();

const detailPanelId = computed(() => `audit-detail-${props.entry.id}`);
const detailHeadingId = computed(() => `${detailPanelId.value}-heading`);
const rowAriaLabel = computed(
  () =>
    `${props.entry.actorLabel} ${props.entry.actionLabel} ${props.entry.subjectLabel}, ${typeLabel.value}, ${props.entry.actorChannelLabel}, ${props.entry.areaLabel}, ${fullDateTimeLabel.value}. ${props.expanded ? "Hide" : "Show"} details.`,
);
const timelineDateTimeLabel = computed(() =>
  formatAuditTimelineDateTime(props.entry.occurredAtUtc),
);
const timelineSourceLabel = computed(() => {
  if (
    props.entry.sourceLabel === "GitHub" ||
    props.entry.sourceLabel === "Notion"
  ) {
    return props.entry.sourceLabel;
  }
  return null;
});
const timeLabel = computed(() =>
  props.showDate
    ? formatAuditTimelineDateTime(props.entry.occurredAtUtc)
    : formatAuditTime(props.entry.occurredAtUtc),
);
const fullDateTimeLabel = computed(() =>
  formatAuditDateTime(props.entry.occurredAtUtc),
);
const typeLabel = computed(() => {
  switch (props.entry.type) {
    case "added":
      return "Added";
    case "deleted":
      return "Deleted";
    default:
      return "Edited";
  }
});
const typeColor = computed<"success" | "error" | "neutral">(() => {
  switch (props.entry.type) {
    case "added":
      return "success";
    case "deleted":
      return "error";
    default:
      return "neutral";
  }
});
const icon = computed(() => {
  switch (props.entry.type) {
    case "added":
      return "i-hugeicons-file-add";
    case "deleted":
      return "i-hugeicons-file-remove";
    default:
      return "i-hugeicons-file-edit";
  }
});
</script>
