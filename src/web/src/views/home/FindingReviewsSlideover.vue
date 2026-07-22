<template>
  <!--
  Drill-down for the Critical/Major findings stat cards: lists review groups
  (deduplicated to the latest attempt per PR/agent session — see the store
  action's comment) with at least one finding of `severity` in the window.
  Each row navigates to the standalone review (or, for PR-backed groups, the
  full PR review history) via a real anchor, not client-side routing — the
  URL is minted server-side and already absolute, so a plain navigation is
  the simplest thing that's guaranteed to land correctly.
  -->
  <USlideover
    v-model:open="open"
    :title="title"
    :description="description"
    side="right"
    :ui="{ content: 'max-w-2xl' }"
  >
    <template #body>
      <div class="flex h-full min-h-96 flex-col gap-3">
        <template v-if="loading">
          <USkeleton v-for="index in 4" :key="index" class="h-14 rounded-md" />
        </template>

        <UListbox
          v-else-if="rows.length > 0"
          :items="rows"
          filter
          filter-placeholder="Filter by title, repo, or author..."
          class="w-full flex-1"
          :ui="{ root: 'flex-1', content: 'flex-1 max-h-none' }"
        >
          <template #item-leading>
            <UIcon
              name="i-hugeicons-alert-02"
              class="size-4"
              :class="severityColor === 'error' ? 'text-error' : 'text-warning'"
            />
          </template>
          <template #item-trailing="{ item }">
            <UBadge :color="severityColor" variant="subtle" size="sm">
              {{ item.count }} {{ severityLabel }}
            </UBadge>
          </template>
        </UListbox>

        <UEmpty
          v-else
          icon="i-hugeicons-alert-02"
          title="No findings"
          :description="`No ${severityLabel} findings in this window.`"
          class="py-16"
        />

        <UButton
          v-if="hasMore"
          label="Load more"
          color="neutral"
          variant="subtle"
          block
          :loading="loadingMore"
          @click="emits('loadMore')"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { useTimeAgo } from "@vueuse/core";
import {
  codeReviewRequestOriginEnum,
  findingSeverityEnum,
  type FindingReviewListItemResponse,
  type FindingSeverity,
} from "@/api/generated";
import { toMetricNumber } from "@/stores/metrics-store";

/** One rendered row's precomputed view model — see the MVVM note in the frontend guidance. */
type FindingReviewRow = {
  /** Stable key for `v-for`/UListbox. */
  value: string;
  label: string;
  description: string;
  count: number;
  /**
   * Whole-row navigation via `ListboxItem.onSelect` rather than an `<a>` nested inside the
   * option — a listbox option already has `role="option"`, and a nested interactive element
   * (a link) inside it is an invalid/confusing ARIA pattern. `onSelect` fires for both click
   * and keyboard (Enter/Space) activation, so this keeps the row's native listbox semantics.
   */
  onSelect: () => void;
};

const open = defineModel<boolean>("open", { required: true });

const props = defineProps<{
  severity: FindingSeverity;
  items: FindingReviewListItemResponse[];
  hasMore: boolean;
  loading: boolean;
  loadingMore: boolean;
}>();

const emits = defineEmits<{
  loadMore: [];
}>();

const severityLabel = computed(() =>
  props.severity === findingSeverityEnum.Major ? "major" : "critical",
);

/**
 * Matches the Critical=error/Major=warning convention already used by the standalone review's
 * severity tabs (`CodeReviewFacetTabs.vue`) — both severities sharing the same red would make a
 * Major-findings slideover visually indistinguishable from a Critical one.
 */
const severityColor = computed<"error" | "warning">(() =>
  props.severity === findingSeverityEnum.Major ? "warning" : "error",
);

const title = computed(
  () => `${severityLabel.value === "major" ? "Major" : "Critical"} findings`,
);
const description = computed(
  () =>
    `Reviews with at least one ${severityLabel.value} finding, newest first.`,
);

/** Precomputes each row's label/description/count so the template stays logic-free. */
const rows = computed<FindingReviewRow[]>(() =>
  props.items.map((item) => ({
    value: item.reviewId,
    label: item.title,
    description: `${repoLabel(item)} · ${item.authorLogin} · ${useTimeAgo(new Date(item.createdAtUtc)).value}`,
    count:
      props.severity === findingSeverityEnum.Major
        ? Math.round(toMetricNumber(item.groupMajorFindings))
        : Math.round(toMetricNumber(item.groupCriticalFindings)),
    onSelect: () => window.location.assign(item.url),
  })),
);

/** "owner/repo #123" for PR-backed reviews; agent (non-PR) reviews have no meaningful PR number. */
function repoLabel(item: FindingReviewListItemResponse): string {
  return item.requestOrigin === codeReviewRequestOriginEnum.Agent
    ? item.ownerQualifiedRepoName
    : `${item.ownerQualifiedRepoName} #${item.pullRequestNumber}`;
}
</script>
