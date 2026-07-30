<template>
  <!-- Shared reviewer-summary renderer for persisted reviews and synthetic agent tests. -->
  <UAccordion
    v-if="items.length > 0"
    type="multiple"
    :default-value="[]"
    :items="items"
    :ui="{
      root: 'rounded-md border border-default bg-default',
      item: 'border-b border-default last:border-b-0',
      trigger: 'px-3 py-2.5 hover:bg-elevated/40',
      label: 'flex-1 min-w-0 text-sm font-medium text-highlighted',
      body: 'px-3 pb-3 pt-0',
    }"
  >
    <template #default="{ item }">
      <div class="grid min-w-0 w-full gap-0.5">
        <div class="flex min-w-0 w-full items-center gap-2">
          <span class="min-w-0 truncate">{{ item.label }}</span>
          <div class="ml-auto flex shrink-0 items-center gap-1.5">
            <UBadge
              v-for="bucket in item.severityBuckets"
              :key="bucket.level"
              :label="`${bucket.count}`"
              :color="bucket.color"
              variant="soft"
              size="sm"
            />
            <UBadge
              :label="item.facet"
              color="neutral"
              variant="subtle"
              size="sm"
            />
          </div>
        </div>
        <p
          v-if="item.summary"
          class="truncate text-xs leading-4 font-normal text-muted"
        >
          {{ item.summary }}
        </p>
      </div>
    </template>

    <template #body="{ item }">
      <div class="code-review-markdown-body grid gap-3">
        <!-- The summary already shows as a dim preview in the trigger; only fall back to
             rendering it in full here when there's no details to show instead. -->
        <Comark
          v-if="item.summary && !item.details"
          :markdown="item.summary"
          :plugins="markdownPlugins"
          class="text-sm text-default"
        />
        <Comark
          v-if="item.details"
          :markdown="item.details"
          :plugins="markdownPlugins"
          class="text-sm text-default"
        />
      </div>
    </template>
  </UAccordion>
</template>

<script setup lang="ts">
import { Comark } from "@comark/vue";
import {
  codeReviewFindingLevelEnum,
  type CodeReviewFindingDto,
  type CodeReviewFindingLevel,
  type CodeReviewReviewerFindingsDto,
} from "@/api/generated";
import { useCodeHighlight } from "@/composables/useCodeHighlight";

type SeverityColor = "error" | "warning" | "neutral" | "info" | "tertiary";

type SeverityBucket = {
  level: CodeReviewFindingLevel;
  count: number;
  color: SeverityColor;
};

type SummaryAccordionItem = {
  label: string;
  value: string;
  icon: string;
  facet: string;
  summary: string;
  details: string;
  severityBuckets: SeverityBucket[];
};

const props = defineProps<{
  reviews: CodeReviewReviewerFindingsDto[];
}>();

const { codeHighlightPlugins: markdownPlugins } = useCodeHighlight();

/** Only reviewers with narrative text are worth a section; pure-findings reviewers are skipped. */
const items = computed<SummaryAccordionItem[]>(() =>
  props.reviews
    .filter((reviewer) => reviewer.summary || reviewer.details)
    .map((reviewer, index) => ({
      label: reviewer.agent,
      value: `${reviewer.facet}:${reviewer.agent}:${index}`,
      icon: "i-hugeicons-ai-programming",
      facet: reviewer.facet,
      summary: reviewer.summary,
      details: reviewer.details,
      severityBuckets: buildSeverityBuckets(reviewer.findings),
    })),
);

/** Ordered highest to lowest so badges render in a stable, severity-descending sequence. */
const severityColorByLevel: [CodeReviewFindingLevel, SeverityColor][] = [
  [codeReviewFindingLevelEnum.Critical, "error"],
  [codeReviewFindingLevelEnum.Major, "warning"],
  [codeReviewFindingLevelEnum.Minor, "neutral"],
  [codeReviewFindingLevelEnum.Suggestion, "info"],
  [codeReviewFindingLevelEnum.Comment, "tertiary"],
];

/** Buckets a reviewer's findings by severity, omitting levels with no findings. */
function buildSeverityBuckets(findings: CodeReviewFindingDto[]): SeverityBucket[] {
  return severityColorByLevel
    .map(([level, color]) => ({
      level,
      color,
      count: findings.filter((finding) => finding.level === level).length,
    }))
    .filter((bucket) => bucket.count > 0);
}
</script>

<style scoped>
.code-review-markdown-body {
  display: grid;
  gap: 0.75rem;
}

.code-review-markdown-body :deep(p) {
  margin: 0;
}

.code-review-markdown-body :deep(code) {
  border-radius: var(--ui-radius-sm);
  background: var(--ui-bg-elevated);
  padding: 0.125rem 0.25rem;
  font-size: 0.8125rem;
}

.code-review-markdown-body :deep(pre) {
  overflow-x: auto;
  border-radius: var(--ui-radius-md);
  border: 1px solid var(--ui-border);
  background: var(--ui-bg-elevated);
  padding: 0.75rem;
}

.code-review-markdown-body :deep(pre code) {
  background: transparent;
  padding: 0;
}
</style>
