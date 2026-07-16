<template>
  <!-- Shows the failure state when the review execution completed with an error. -->
  <UAlert
    v-if="review.status === codeReviewStatusEnum.Errored"
    title="Code review failed"
    :description="
      review.failureMessage ||
      'This code review failed before findings could be produced.'
    "
    icon="i-hugeicons-alert-02"
    color="warning"
    variant="subtle"
  />

  <!-- Shows the clean completed state when there are no findings to review. -->
  <UAlert
    v-else-if="
      review.status === codeReviewStatusEnum.Completed &&
      totalFindings(review) === 0
    "
    title="No findings"
    description="This review completed cleanly with no critical, major, minor, suggestion, or comment findings."
    icon="i-hugeicons-checkmark-circle-02"
    color="success"
    variant="subtle"
  />

  <!-- Shows the pending state before findings are available from the review artifact. -->
  <UAlert
    v-else-if="totalFindings(review) === 0"
    title="Review pending"
    :description="`This code review is ${review.status.toLowerCase()}; findings will appear after it completes.`"
    icon="i-hugeicons-clock-01"
    color="info"
    variant="subtle"
  />

  <!-- Shows skeleton cards while the detailed findings artifact is loading. -->
  <div v-else-if="loading" class="grid gap-3">
    <USkeleton v-for="index in 2" :key="index" class="h-24 rounded-md" />
  </div>

  <!-- Shows the findings load error when the review exists but detail hydration failed. -->
  <UAlert
    v-else-if="error"
    title="Could not load finding details"
    :description="error"
    icon="i-hugeicons-alert-02"
    color="error"
    variant="subtle"
  />

  <!-- Shows a celebration banner when the review completed successfully with findings. -->
  <div v-else>
    <div
      v-if="
        review.status === codeReviewStatusEnum.Completed &&
        totalFindings(review) === 0
      "
      class="mb-4 flex items-center gap-2 rounded-md bg-success/10 px-4 py-2"
    >
      {{ celebrationEmoji }} LGTM
    </div>

    <UTabs
      :items="items"
      :default-value="defaultTabValue"
      variant="link"
      color="neutral"
      :ui="{ label: 'overflow-visible', content: 'pt-4' }"
    >
      <!-- Renders each tab header with the severity label and a non-zero finding count. -->
      <template #default="{ item }">
        <span v-if="item.count" class="px-2"
          >{{ item.label }}

          <UBadge
            :label="`${item.count}`"
            :color="item.color"
            variant="subtle"
            size="sm"
            class="ml-1"
          />
        </span>
        <span v-else class="px-2 font-light">{{ item.label }}</span>
      </template>

      <!-- Renders the selected severity tab content for its reviewer findings bucket. -->
      <template #content="{ item }">
        <!-- Shows an empty severity bucket with the count and explanatory description. -->
        <div
          v-if="reviewerSectionsByLevel[item.level].length === 0"
          class="grid gap-3 rounded-md bg-elevated/30 p-4"
        >
          <div class="flex flex-wrap items-center gap-2">
            <UBadge
              :label="`${item.count}`"
              :color="item.color"
              variant="subtle"
              class="rounded-full"
            />
            <span class="text-sm font-medium text-highlighted">
              {{ item.label }} findings
            </span>
          </div>
          <p class="text-sm text-muted">{{ item.description }}</p>
        </div>

        <!-- Groups matching findings by reviewer facet when this severity has results. -->
        <div v-else class="grid gap-4">
          <section
            v-for="(section, sectionIndex) in reviewerSectionsByLevel[
              item.level
            ]"
            :key="reviewerSectionKey(item.level, section, sectionIndex)"
            class="rounded-md border border-default bg-elevated/20 p-4"
          >
            <!-- Shows the reviewer facet name and the agent that produced these findings. -->
            <div
              class="flex min-w-0 flex-wrap items-center gap-2 cursor-pointer select-none"
              @click="toggleReviewerSection(item.level, section, sectionIndex)"
            >
              <span class="text-md font-semibold text-highlighted">
                {{ section.reviewer.agent }}
              </span>
              <!-- <UBadge
                :label="section.reviewer.agent"
                color="neutral"
                variant="outline"
                class="rounded-full"
              /> -->
              <UTooltip text="Copy XML guidance" class="ml-auto">
                <UButton
                  :icon="
                    isReviewerSectionCopied(item.level, section, sectionIndex)
                      ? 'i-hugeicons-checkmark-circle-02'
                      : 'i-hugeicons-copy-01'
                  "
                  color="neutral"
                  variant="ghost"
                  size="sm"
                  square
                  :aria-label="`Copy ${section.reviewer.facet} guidance XML`"
                  @click.stop="
                    copyReviewerSectionGuidance(
                      item.level,
                      section,
                      sectionIndex,
                    )
                  "
                />
              </UTooltip>
              <UButton
                :icon="
                  isReviewerSectionOpen(item.level, section, sectionIndex)
                    ? 'i-hugeicons-minus-sign-square'
                    : 'i-hugeicons-add-square'
                "
                label="View"
                color="neutral"
                variant="ghost"
                size="sm"
                square
                :aria-label="
                  isReviewerSectionOpen(item.level, section, sectionIndex)
                    ? `Collapse ${section.reviewer.facet} review body`
                    : `Expand ${section.reviewer.facet} review body`
                "
                :aria-expanded="
                  isReviewerSectionOpen(item.level, section, sectionIndex)
                "
                @click.stop="
                  toggleReviewerSection(item.level, section, sectionIndex)
                "
              />
            </div>

            <!-- Keeps each reviewer body independently collapsible while defaulting the first open. -->
            <UCollapsible
              :open="isReviewerSectionOpen(item.level, section, sectionIndex)"
              :class="
                isReviewerSectionOpen(item.level, section, sectionIndex)
                  ? 'mt-2'
                  : ''
              "
              @update:open="
                setReviewerSectionOpen(
                  item.level,
                  section,
                  sectionIndex,
                  $event,
                )
              "
            >
              <template #content>
                <div class="grid gap-3">
                  <!-- TODO: Refactor this into a separate "By reviewer" view because it's confusing in this view
                <div
                  v-if="section.reviewer.summary || section.reviewer.details"
                  class="grid gap-1 text-sm"
                >
                  <Comark
                    v-if="section.reviewer.summary"
                    :markdown="section.reviewer.summary"
                    :plugins="markdownPlugins"
                    class="code-review-markdown-body text-default"
                  />
                  <Comark
                    v-if="section.reviewer.details"
                    :markdown="section.reviewer.details"
                    :plugins="markdownPlugins"
                    class="code-review-markdown-body text-muted"
                  />
                </div>
                -->

                  <!-- Renders every finding card for this reviewer within the selected severity. -->
                  <div class="grid gap-3">
                    <article
                      v-for="finding in section.findings"
                      :key="`${finding.file}:${finding.line ?? 'no-line'}:${finding.summary}`"
                      class="grid gap-2 rounded-md bg-default/60 pt-4"
                    >
                      <div class="flex justify-end gap-2">
                        <UBadge
                          :label="finding.level"
                          :color="item.color"
                          variant="subtle"
                          class="shrink-0 rounded-full"
                        />
                        <AddToCartButton
                          class="shrink-0"
                          :finding="finding"
                          :reviewer="section.reviewer"
                          :is-in-cart="isFindingInCart(finding)"
                          :is-toggling="
                            togglingFindingKey === findingKey(finding)
                          "
                          @toggle-cart="handleToggleCart"
                        />
                      </div>

                      <!-- Shows the finding summary, severity badge, and cart button on one row. -->
                      <p
                        class="min-w-0 flex-1 break-words text-sm font-medium text-highlighted"
                      >
                        {{ finding.summary }}
                      </p>

                      <!-- Shows the source file, line, and diff side for the finding. -->
                      <p class="break-all font-mono text-xs text-muted">
                        {{ locationLabel(finding) }}
                      </p>

                      <!-- Renders the reviewer-provided markdown body, including code fences. -->
                      <Comark
                        :markdown="finding.body"
                        :plugins="markdownPlugins"
                        class="code-review-markdown-body text-sm text-default"
                      />
                    </article>
                  </div>
                </div>
              </template>
            </UCollapsible>
          </section>

          <!-- Collapse all expanded reviewer sections for this severity tab. -->
          <div
            v-if="hasAnyOpenSectionForLevel(item.level)"
            class="flex justify-end"
          >
            <UButton
              label="Collapse all"
              icon="i-hugeicons-minus-sign-square"
              color="neutral"
              variant="ghost"
              size="sm"
              @click="collapseAllSections(item.level)"
            />
          </div>
        </div>
      </template>
    </UTabs>
  </div>
</template>

<script setup lang="ts">
import { Comark } from "@comark/vue";
import { useClipboard } from "@vueuse/core";
import {
  codeReviewFindingLevelEnum,
  codeReviewStatusEnum,
  type CodeReviewFindingDto,
  type CodeReviewFindingLevel,
  type CodeReviewFindingsResponse,
  type CodeReviewRecordDto,
  type CodeReviewReviewerFindingsDto,
} from "@/api/generated";
import AddToCartButton from "@/views/code-reviews/pull-requests/AddToCartButton.vue";
import { computeFindingContentHash } from "@/composables/useFindingContentHash";
import { useCodeHighlight } from "@/composables/useCodeHighlight";

type SeverityItem = {
  label: string;
  value: string;
  level: CodeReviewFindingLevel;
  count: number;
  color: "error" | "warning" | "neutral" | "info";
  disabled: boolean;
  description: string;
};

type ReviewerFindingSection = {
  reviewer: CodeReviewReviewerFindingsDto;
  findings: CodeReviewFindingDto[];
};

/**
 * Receives the review row, optional hydrated findings artifact, and load state
 * from the pull request review accordion.
 */
const props = defineProps<{
  review: CodeReviewRecordDto;
  findings: CodeReviewFindingsResponse | null;
  loading: boolean;
  error: string | null;
  cartContentHashes: Set<string>;
}>();

const emits = defineEmits<{
  toggleCart: [
    finding: CodeReviewFindingDto,
    reviewer: CodeReviewReviewerFindingsDto,
    review: CodeReviewRecordDto,
    annotation: string | null,
  ];
}>();

/** Tracks controlled UCollapsible state per severity/reviewer section. */
const reviewerSectionOpenByKey = ref<Record<string, boolean>>({});

/** Clipboard helper used by the reviewer-section copy buttons. */
const { copied, copy } = useClipboard({ copiedDuring: 1500, legacy: true });
const copiedReviewerSectionKey = ref<string | null>(null);

// ── Cart state ─────────────────────────────────────────────────────────

/** Pre-computed finding content hashes keyed by finding key string. */
const findingHashByKey = ref<Record<string, string>>({});

/** Which finding key is currently being toggled (loading state). */
const togglingFindingKey = ref<string | null>(null);

/**
 * Recomputes finding hashes whenever the findings response changes so
 * "In cart" badge matching is always current.
 */
watch(
  () => props.findings,
  async (findings) => {
    if (!findings) return;
    const next: Record<string, string> = {};
    for (const reviewer of findings.reviews) {
      for (const finding of reviewer.findings) {
        next[findingKey(finding)] = await computeFindingContentHash(finding);
      }
    }
    findingHashByKey.value = next;
  },
  { immediate: true },
);

/** Checks whether a finding is in the active draft cart. */
function isFindingInCart(finding: CodeReviewFindingDto): boolean {
  const hash = findingHashByKey.value[findingKey(finding)];
  return hash !== undefined && props.cartContentHashes.has(hash);
}

/** Stable key for a finding, used for :key bindings and hash lookups. */
function findingKey(finding: CodeReviewFindingDto): string {
  return `${finding.file}:${finding.line ?? "no-line"}:${finding.summary}`;
}

/**
 * Emits the toggle-cart event and tracks the toggling loading state by finding key.
 */
async function handleToggleCart(
  finding: CodeReviewFindingDto,
  reviewer: CodeReviewReviewerFindingsDto,
  annotation: string | null,
) {
  const key = findingKey(finding);
  togglingFindingKey.value = key;
  try {
    emits("toggleCart", finding, reviewer, props.review, annotation);
  } finally {
    togglingFindingKey.value = null;
  }
}

/**
 * Groups hydrated findings by severity while preserving the reviewer facet
 * context that is shown in each tab panel.
 */
const reviewerSectionsByLevel = computed<
  Record<CodeReviewFindingLevel, ReviewerFindingSection[]>
>(() => {
  const result = emptyReviewerSectionsByLevel();

  if (!props.findings) {
    return result;
  }

  for (const reviewer of props.findings.reviews) {
    for (const level of severityLevels) {
      const findings = reviewer.findings.filter(
        (finding) => finding.level === level,
      );

      if (findings.length > 0) {
        result[level].push({ reviewer, findings });
      }
    }
  }

  return result;
});

/**
 * Builds the severity tab model used by UTabs, including counts, colors, and
 * empty-state descriptions for each tab. Empty tabs are disabled.
 */
const items = computed<SeverityItem[]>(() => [
  {
    label: "Critical",
    value: "critical",
    level: codeReviewFindingLevelEnum.Critical,
    count: countForLevel(codeReviewFindingLevelEnum.Critical),
    color: "error",
    disabled: countForLevel(codeReviewFindingLevelEnum.Critical) === 0,
    description:
      "Blocking correctness or safety issues that require immediate attention.",
  },
  {
    label: "Major",
    value: "major",
    level: codeReviewFindingLevelEnum.Major,
    count: countForLevel(codeReviewFindingLevelEnum.Major),
    color: "warning",
    disabled: countForLevel(codeReviewFindingLevelEnum.Major) === 0,
    description:
      "High-confidence defects or design problems that should be addressed before merge.",
  },
  {
    label: "Minor",
    value: "minor",
    level: codeReviewFindingLevelEnum.Minor,
    count: countForLevel(codeReviewFindingLevelEnum.Minor),
    color: "neutral",
    disabled: countForLevel(codeReviewFindingLevelEnum.Minor) === 0,
    description: "Low-risk issues, maintainability nits, or focused test gaps.",
  },
  {
    label: "Suggestions",
    value: "suggestions",
    level: codeReviewFindingLevelEnum.Suggestion,
    count: countForLevel(codeReviewFindingLevelEnum.Suggestion),
    color: "info",
    disabled: countForLevel(codeReviewFindingLevelEnum.Suggestion) === 0,
    description:
      "Optional improvements that may make the change easier to maintain.",
  },
  {
    label: "Comments",
    value: "comments",
    level: codeReviewFindingLevelEnum.Comment,
    count: countForLevel(codeReviewFindingLevelEnum.Comment),
    color: "neutral",
    disabled: countForLevel(codeReviewFindingLevelEnum.Comment) === 0,
    description: "Informational notes preserved from the reviewer output.",
  },
]);

/**
 * Selects the first severity tab that has findings so the active tab always
 * shows content when the review has any findings at all.
 *
 * NOTE: UTabs' `default-value` is only applied at mount (uncontrolled), so
 * this only affects the tab selected when the component first renders. It
 * won't re-select the active tab if `items` changes later, e.g. from a
 * findings refetch while the tabs are already mounted.
 */
const defaultTabValue = computed(() => {
  return items.value.find((item) => !item.disabled)?.value ?? "critical";
});

/** Picks a random celebration emoji for the completed-review banner. */
const celebrationEmoji = ref(
  ["🎉", "🥳", "🚀", "✨", "💯", "🏆"][Math.floor(Math.random() * 6)],
);

/**
 * Defines the stable ordering used for both tab rendering and reviewer finding
 * grouping.
 */
const severityLevels: CodeReviewFindingLevel[] = [
  codeReviewFindingLevelEnum.Critical,
  codeReviewFindingLevelEnum.Major,
  codeReviewFindingLevelEnum.Minor,
  codeReviewFindingLevelEnum.Suggestion,
  codeReviewFindingLevelEnum.Comment,
];

/**
 * Seeds each severity tab so its first reviewer section starts open and any
 * additional reviewer sections start collapsed without overwriting user toggles.
 */
watch(
  reviewerSectionsByLevel,
  (sectionsByLevel) => {
    const nextOpenByKey: Record<string, boolean> = {};

    for (const level of severityLevels) {
      sectionsByLevel[level].forEach((section, sectionIndex) => {
        const key = reviewerSectionKey(level, section, sectionIndex);
        nextOpenByKey[key] =
          reviewerSectionOpenByKey.value[key] ?? sectionIndex === 0;
      });
    }

    reviewerSectionOpenByKey.value = nextOpenByKey;
  },
  { immediate: true },
);

/**
 * Adds Comark syntax highlighting for reviewer markdown bodies while keeping
 * the rendered code block styling controlled by this component.
 */
const { codeHighlightPlugins: markdownPlugins } = useCodeHighlight();

/**
 * Returns the finding count for a severity, preferring hydrated artifact data
 * when it has loaded and falling back to aggregate counts from the review row.
 */
function countForLevel(level: CodeReviewFindingLevel): number {
  if (!props.findings) {
    return aggregateCountForLevel(level);
  }

  return reviewerSectionsByLevel.value[level].reduce(
    (count, section) => count + section.findings.length,
    0,
  );
}

/**
 * Reads the precomputed aggregate finding count for a severity from the review
 * row returned by the pull request review list.
 */
function aggregateCountForLevel(level: CodeReviewFindingLevel): number {
  if (level === codeReviewFindingLevelEnum.Critical) {
    return toNumber(props.review.criticalFindings);
  }

  if (level === codeReviewFindingLevelEnum.Major) {
    return toNumber(props.review.majorFindings);
  }

  if (level === codeReviewFindingLevelEnum.Minor) {
    return toNumber(props.review.minorFindings);
  }

  if (level === codeReviewFindingLevelEnum.Suggestion) {
    return toNumber(props.review.suggestionFindings);
  }

  return toNumber(props.review.commentFindings);
}

/**
 * Creates an empty severity-to-reviewer-section map so template lookups always
 * have a stable array for every backend finding level.
 */
function emptyReviewerSectionsByLevel(): Record<
  CodeReviewFindingLevel,
  ReviewerFindingSection[]
> {
  return {
    [codeReviewFindingLevelEnum.Critical]: [],
    [codeReviewFindingLevelEnum.Major]: [],
    [codeReviewFindingLevelEnum.Minor]: [],
    [codeReviewFindingLevelEnum.Suggestion]: [],
    [codeReviewFindingLevelEnum.Comment]: [],
  };
}

/**
 * Creates a stable key for the local collapse state of one reviewer section.
 */
function reviewerSectionKey(
  level: CodeReviewFindingLevel,
  section: ReviewerFindingSection,
  sectionIndex: number,
): string {
  return `${level}:${sectionIndex}:${section.reviewer.facet}:${section.reviewer.agent}`;
}

/**
 * Reads whether a reviewer section body is currently expanded.
 */
function isReviewerSectionOpen(
  level: CodeReviewFindingLevel,
  section: ReviewerFindingSection,
  sectionIndex: number,
): boolean {
  const key = reviewerSectionKey(level, section, sectionIndex);

  return reviewerSectionOpenByKey.value[key] ?? sectionIndex === 0;
}

/**
 * Updates the controlled UCollapsible state for a reviewer section.
 */
function setReviewerSectionOpen(
  level: CodeReviewFindingLevel,
  section: ReviewerFindingSection,
  sectionIndex: number,
  open: boolean,
): void {
  const key = reviewerSectionKey(level, section, sectionIndex);

  reviewerSectionOpenByKey.value = {
    ...reviewerSectionOpenByKey.value,
    [key]: open,
  };
}

/**
 * Returns true when at least one reviewer section in this severity level is expanded.
 */
function hasAnyOpenSectionForLevel(level: CodeReviewFindingLevel): boolean {
  return reviewerSectionsByLevel.value[level].some((section, sectionIndex) =>
    isReviewerSectionOpen(level, section, sectionIndex),
  );
}

/**
 * Collapses every expanded reviewer section for the given severity level.
 */
function collapseAllSections(level: CodeReviewFindingLevel): void {
  const next = { ...reviewerSectionOpenByKey.value };
  reviewerSectionsByLevel.value[level].forEach((section, sectionIndex) => {
    next[reviewerSectionKey(level, section, sectionIndex)] = false;
  });
  reviewerSectionOpenByKey.value = next;
}

/**
 * Toggles the reviewer section body from the right-aligned icon button.
 */
function toggleReviewerSection(
  level: CodeReviewFindingLevel,
  section: ReviewerFindingSection,
  sectionIndex: number,
): void {
  setReviewerSectionOpen(
    level,
    section,
    sectionIndex,
    !isReviewerSectionOpen(level, section, sectionIndex),
  );
}

/**
 * Copies the raw reviewer guidance text for one facet section.
 */
async function copyReviewerSectionGuidance(
  level: CodeReviewFindingLevel,
  section: ReviewerFindingSection,
  sectionIndex: number,
): Promise<void> {
  const key = reviewerSectionKey(level, section, sectionIndex);

  await copy(formatReviewerSectionGuidance(section));
  copiedReviewerSectionKey.value = key;
}

/**
 * Checks whether the given section is the most recently copied guidance block.
 */
function isReviewerSectionCopied(
  level: CodeReviewFindingLevel,
  section: ReviewerFindingSection,
  sectionIndex: number,
): boolean {
  const key = reviewerSectionKey(level, section, sectionIndex);

  return copied.value && copiedReviewerSectionKey.value === key;
}

/**
 * Recreates the reviewer guidance as XML from the parsed DTO fields.
 */
function formatReviewerSectionGuidance(
  section: ReviewerFindingSection,
): string {
  const findingXml = section.findings
    .map((finding) => formatFindingGuidanceXml(finding, 6))
    .join("\n");

  return `<code_review_finding>
  <!--
  <instruction_for_agents>
  The following XML is the raw output from expert code reviewers analyzing the PR.
  - Review each finding in the feedback from the expert reviewers
  - Evaluate the change in the broader context of the codebase; the reviewer only saw the PR contents and not the broader codebase; determine the veracity of each finding
  - If a behavior is expected, suggest leaving a comment in the code to explain the rationale to future travelers (agents)
  - The change proposals are high level; plan out specific code changes needed to implement the feedback, finding by finding
  - ALWAYS get confirmation and acceptance of the proposed fix for each finding before writing code
  - ALWAYS ensure there is enough clarity to make the best fix if there is ambiguity or insufficient feedback to confidently implement the change
  - Call out any tradeoffs or shortcomings in the proposed changes if any (especially in the broader context of the codebase)
  - Present the concrete changes needed and let the user decide which to proceed with; do not make changes without confirmation
  - Use a checklist/todo list to keep track of work against this set of findings; we do not want to wing it here
  - Review Zeeq documentation for guidance on best practices
  - Add code comment: "NOTE: (Reason to defer or ignore a finding goes here)" to document any rationale for deferring or ignoring a finding
  </instruction_for_agents>
  -->
  <review
    facet="${escapeXmlAttribute(section.reviewer.facet)}"
    agent="${escapeXmlAttribute(section.reviewer.agent)}">
    <findings>
${findingXml}
    </findings>
  </review>
</code_review_finding>`;
}

/**
 * Formats one finding DTO as the canonical finding XML shape.
 */
function formatFindingGuidanceXml(
  finding: CodeReviewFindingDto,
  indentSize: number,
): string {
  const indent = " ".repeat(indentSize);
  const attributes = [
    `level="${escapeXmlAttribute(finding.level)}"`,
    `summary="${escapeXmlAttribute(finding.summary)}"`,
    `file="${escapeXmlAttribute(finding.file)}"`,
    finding.line ? `line="${escapeXmlAttribute(String(finding.line))}"` : null,
    finding.side ? `side="${escapeXmlAttribute(finding.side)}"` : null,
  ]
    .filter((attribute) => attribute !== null)
    .join(" ");

  return `${indent}<finding ${attributes}><![CDATA[${escapeCdata(finding.body)}]]></finding>`;
}

/**
 * Escapes XML attribute values reconstructed from DTO strings.
 */
function escapeXmlAttribute(value: string): string {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}

/**
 * Prevents copied finding bodies from terminating the CDATA section early.
 */
function escapeCdata(value: string): string {
  return value.replaceAll("]]>", "]]]]><![CDATA[>");
}

/**
 * Formats a finding source location for the monospace metadata line shown
 * inside each visible finding card.
 */
function locationLabel(finding: CodeReviewFindingDto): string {
  const parts = [`File: ${finding.file}`];

  if (finding.line) {
    parts.push(`Line: ${finding.line}`);
  }

  if (finding.side) {
    parts.push(`Side: ${finding.side}`);
  }

  return parts.join(" | ");
}

/**
 * Sums all aggregate finding counts on a review row to choose between pending,
 * clean, and tabbed findings states.
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
 * Normalizes generated API numeric fields that may arrive as strings into safe
 * numbers for arithmetic and count display.
 */
function toNumber(value: number | string): number {
  return typeof value === "number" ? value : Number(value) || 0;
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
