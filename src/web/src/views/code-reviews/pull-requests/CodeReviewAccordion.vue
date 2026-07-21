<template>
  <!-- Empty state: shown when no reviews exist for this pull request -->
  <UEmpty
    v-if="reviews.length === 0"
    icon="i-hugeicons-message-programming"
    title="No reviews yet"
    description="Request a review to queue the first automated pass for this pull request."
    class="py-16"
  />

  <!-- Accordion listing each code review with expandable findings -->
  <UAccordion
    v-else
    type="single"
    :model-value="openReviewId"
    :items="accordionItems"
    :ui="{
      root: 'divide-y divide-default border-b border-default',
      item: 'p-0',
      trigger: 'px-4 py-4 hover:bg-elevated/40 sm:px-6',
      label: 'flex-1 min-w-0',
      content: 'px-4 py-4 sm:px-6',
      body: 'grid gap-3 pt-0 pb-0',
    }"
    @update:model-value="handleOpenReviewChange"
  >
    <!-- Accordion trigger: review label on the left, badges pushed to the right -->
    <template #default="{ item }">
      <div class="flex min-w-0 w-full items-center gap-2">
        <span
          class="min-w-0 truncate text-sm font-semibold text-highlighted font-mono"
        >
          {{ item.label }}
        </span>
        <div class="ml-auto flex shrink-0 items-center gap-2">
          <UBadge
            :label="item.review.status"
            :color="item.statusColor"
            variant="subtle"
            class="rounded-full"
          />
          <UBadge
            :label="`${item.totalFindings} findings`"
            color="neutral"
            variant="outline"
            class="rounded-full"
          />
        </div>
      </div>
    </template>

    <!-- Accordion body: facet-tabbed findings, lazy-loaded on first open -->
    <template #body="{ item, open }">
      <CodeReviewFacetTabs
        v-if="open"
        :key="`${item.reviewKey}:${facetTabsResetKey}`"
        :review="item.review"
        :findings="item.findings"
        :loading="item.loadingFindings"
        :error="item.findingsError"
        :cart-content-hashes="cartContentHashes"
        @toggle-cart="
          (finding, reviewer, review, annotation) =>
            emits('toggleCart', finding, reviewer, review, annotation)
        "
      />

      <!-- Documents/snippets the reviewers consulted; renders below the facet tabs
           for every open review, including clean ones with telemetry but no findings. -->
      <CodeReviewSourcesPanel
        v-if="open && item.findings?.sourceTelemetry"
        :source-telemetry="item.findings.sourceTelemetry"
      />
    </template>
  </UAccordion>
</template>

<script setup lang="ts">
import type {
  CodeReviewFindingDto,
  CodeReviewFindingsResponse,
  CodeReviewRecordDto,
  CodeReviewReviewerFindingsDto,
} from "@/api/generated";
import { reviewFindingsKey } from "@/stores/code-review-store";

import CodeReviewFacetTabs from "./CodeReviewFacetTabs.vue";
import CodeReviewSourcesPanel from "./CodeReviewSourcesPanel.vue";

type CodeReviewStatusColor =
  | "success"
  | "warning"
  | "error"
  | "neutral"
  | "info"
  | "tertiary";

type CodeReviewAccordionItemViewModel = {
  label: string;
  value: string;
  review: CodeReviewRecordDto;
  reviewKey: string;
  totalFindings: number;
  statusColor: CodeReviewStatusColor;
  findings: CodeReviewFindingsResponse | null;
  loadingFindings: boolean;
  findingsError: string | null;
};

const props = defineProps<{
  reviews: CodeReviewRecordDto[];
  reviewFindingsByReviewKey: Record<string, CodeReviewFindingsResponse>;
  loadingReviewFindingsByReviewKey: Record<string, boolean>;
  reviewFindingsErrorsByReviewKey: Record<string, string>;
  cartContentHashes: Set<string>;
}>();

const emits = defineEmits<{
  loadReviewFindings: [review: CodeReviewRecordDto];
  toggleCart: [
    finding: CodeReviewFindingDto,
    reviewer: CodeReviewReviewerFindingsDto,
    review: CodeReviewRecordDto,
    annotation: string | null,
  ];
}>();

/** Two-way bound model reflecting the currently open accordion item. */
const openReviewId = defineModel<string>("openReviewId");

/** Incremented each time a new review is opened to force CodeReviewFacetTabs remount. */
const facetTabsResetKey = ref(0);

/** View model cache for the accordion template and lazy-load decisions. */
const accordionItems = computed<CodeReviewAccordionItemViewModel[]>(() =>
  props.reviews.map((review) => createAccordionItemViewModel(review)),
);

/**
 * Handles accordion open/close from user interaction.
 * Only updates the two-way model; side effects run in the watch below.
 *
 * @param value - The accordion model value (single-select string or undefined).
 */
function handleOpenReviewChange(value: string | string[] | undefined) {
  openReviewId.value = Array.isArray(value) ? value[0] : value;
}

/**
 * Emits a request to load detailed findings for the given review. Loads when the
 * review has findings to hydrate OR source telemetry to show, and there is an
 * artifact or telemetry payload to fetch. Skips reviews already loaded/loading.
 *
 * @param reviewId - The ID of the review whose findings should be loaded.
 */
function loadFindingsForOpenReview(reviewId: string) {
  const item = accordionItems.value.find((item) => item.value === reviewId);

  if (!item || (item.totalFindings === 0 && !item.review.hasSourceTelemetry)) {
    return;
  }

  if (!item.review.findingsStorageUri && !item.review.hasSourceTelemetry) {
    return;
  }

  if (item.findings || item.loadingFindings) {
    return;
  }

  emits("loadReviewFindings", item.review);
}

/**
 * Runs side effects when the accordion model changes — from user interaction
 * or from the parent (URL deep link / back-forward). Guards against duplicate
 * runs when the parent echoes the same value back after URL sync.
 *
 * `immediate: true` is required because a hard refresh (or landing directly
 * on a deep-linked URL) mounts this component with `openReviewId` already
 * set to its final value — there's no change event to observe, so without
 * `immediate` the initial review's findings would never be requested.
 *
 * Declared after accordionItems and loadFindingsForOpenReview to avoid
 * accessing them before initialization when the callback fires during setup.
 */
watch(
  openReviewId,
  (reviewId, oldReviewId) => {
    if (reviewId && reviewId !== oldReviewId) {
      facetTabsResetKey.value += 1;
      loadFindingsForOpenReview(reviewId);
    }
  },
  { immediate: true },
);

/**
 * Creates the data shape consumed by the template so derived values are cached
 * by Vue instead of recomputed from slot expressions.
 *
 * @param review - The review record.
 * @returns A single accordion item view model.
 */
function createAccordionItemViewModel(
  review: CodeReviewRecordDto,
): CodeReviewAccordionItemViewModel {
  const key = reviewFindingsKey(review);
  const findingsCount = totalFindings(review);

  return {
    label: `${formatDate(review.createdAtUtc)}`,
    value: review.id,
    review,
    reviewKey: key,
    totalFindings: findingsCount,
    statusColor: statusColor(review, findingsCount),
    findings: props.reviewFindingsByReviewKey[key] ?? null,
    loadingFindings: props.loadingReviewFindingsByReviewKey[key] === true,
    findingsError: props.reviewFindingsErrorsByReviewKey[key] ?? null,
  };
}

/**
 * Maps backend lifecycle status into stable Nuxt UI semantic colors.
 *
 * @param item - The review record.
 * @param findingsCount - Total findings already computed for this review.
 * @returns A Nuxt UI semantic color token.
 */
function statusColor(
  item: CodeReviewRecordDto,
  findingsCount: number,
): CodeReviewStatusColor {
  const status = item.status;

  if (status === "Completed" && findingsCount === 0) {
    return "success";
  }

  if (status === "Completed" && findingsCount > 0) {
    if (item.criticalFindings > 0) {
      return "error";
    }

    if (item.majorFindings > 0) {
      return "warning";
    }

    if (item.minorFindings > 0) {
      return "neutral";
    }

    if (item.suggestionFindings > 0) {
      return "info";
    }

    if (item.commentFindings > 0) {
      return "tertiary";
    }
  }

  if (status === "Pending") {
    return "warning";
  }

  if (status === "Running") {
    return "info";
  }

  if (status === "Errored") {
    return "error";
  }

  return "neutral";
}

/**
 * Sums all finding severities into a single total.
 *
 * @param review - The review record.
 * @returns Total number of findings across all severity levels.
 */
function totalFindings(review: CodeReviewRecordDto): number {
  return (
    toNumber(review.criticalFindings) +
    toNumber(review.majorFindings) +
    toNumber(review.minorFindings) +
    toNumber(review.suggestionFindings) +
    toNumber(review.commentFindings)
  );
}

/**
 * Coerces a value that may be a number or string into a number, defaulting to 0.
 *
 * @param value - The value to coerce.
 * @returns The numeric value, or 0 if unparseable.
 */
function toNumber(value: number | string): number {
  return typeof value === "number" ? value : Number(value) || 0;
}

/**
 * Formats a UTC date into a locale-aware short date-time string.
 *
 * @param value - The date to format.
 * @returns A human-readable date string like "Jun 26, 2:30 PM".
 */
function formatDate(value: Date): string {
  return new Intl.DateTimeFormat(undefined, {
    weekday: "short",
    month: "short",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).format(new Date(value));
}
</script>
