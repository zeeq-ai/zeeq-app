<template>
  <!-- Results are deliberately ephemeral: this panel renders exactly what the test-run API returned. -->
  <div class="h-full min-h-0 w-full max-w-full overflow-y-auto pb-3">
    <UEmpty
      v-if="!result"
      icon="i-hugeicons-chart-evaluation"
      title="No test results"
      description="Run the current agent draft against a pull request to see what it would produce."
      class="py-16"
    />

    <div v-else class="grid min-w-0 max-w-full gap-4">
      <div class="flex min-w-0 flex-wrap items-start justify-between gap-3">
        <div class="min-w-0">
          <h3 class="truncate text-sm font-semibold text-highlighted">
            #{{ result.pullRequest.pullRequestNumber }}
            {{ result.pullRequest.title }}
          </h3>
          <p class="mt-1 text-sm text-muted">
            {{ result.pullRequest.ownerQualifiedRepoName }} ·
            {{ result.pullRequest.authorLogin }} ·
            {{ result.pullRequest.branch }} →
            {{ result.pullRequest.baseBranch }}
          </p>
        </div>
        <div class="flex shrink-0 flex-wrap justify-end gap-2">
          <UBadge
            v-if="result.pullRequest.isDraft"
            label="Draft"
            color="warning"
            variant="subtle"
            class="rounded-full"
          />
          <UBadge
            :label="result.resultKind"
            color="neutral"
            variant="subtle"
            class="rounded-full"
          />
          <UButton
            label="View PR"
            icon="i-hugeicons-github"
            color="neutral"
            variant="subtle"
            size="sm"
            :to="result.pullRequest.htmlUrl"
            target="_blank"
          />
        </div>
      </div>

      <div class="grid min-w-0 grid-cols-1 gap-2 sm:grid-cols-2 xl:grid-cols-6">
        <div
          v-for="metric in summaryMetrics"
          :key="metric.label"
          class="min-w-0 rounded-md border border-default bg-elevated/20 px-3 py-2"
          :class="{ 'xl:col-span-3': metric.wide }"
        >
          <p class="text-xs text-muted">{{ metric.label }}</p>
          <p class="mt-1 font-mono text-sm text-highlighted">
            {{ metric.value }}
          </p>
        </div>
      </div>

      <UEmpty
        v-if="
          result.resultKind ===
          codeReviewAgentTestRunResultKindEnum.NoFilesInScope
        "
        icon="i-hugeicons-filter-remove"
        title="No files in scope"
        description="Repository-level filters excluded every changed file before this agent could run."
        class="py-12"
      />

      <UEmpty
        v-else-if="
          result.resultKind ===
          codeReviewAgentTestRunResultKindEnum.NoAgentActivation
        "
        icon="i-hugeicons-filter-edit"
        title="No agent activation"
        description="The repository had files in scope, but this draft agent's activation filters did not match any of them."
        class="py-12"
      />

      <UTabs
        v-else
        :items="resultTabs"
        :default-value="defaultResultTab"
        variant="link"
        color="neutral"
        class="min-w-0 max-w-full"
        :ui="{ label: 'overflow-visible', content: 'pt-4' }"
      >
        <template #default="{ item }">
          <span
            v-if="item.value === sourcesTabValue"
            class="px-2"
            :class="{ 'font-light': item.disabled }"
          >
            {{ item.label }}
          </span>
          <span v-else-if="item.count" class="px-2">
            {{ item.label }}
            <UBadge
              :label="`${item.count}`"
              :color="item.color"
              variant="soft"
              size="sm"
              class="ml-1"
            />
          </span>
          <span v-else class="px-2 font-light">{{ item.label }}</span>
        </template>

        <template #content="{ item }">
          <div v-if="item.value === sourcesTabValue" class="grid gap-4">
            <CodeReviewSummaryAccordion
              :reviews="result.findings.reviews ?? []"
            />

            <CodeReviewSourceTelemetryAccordion
              v-if="hasSourceTelemetryContent"
              :source-telemetry="result.findings.sourceTelemetry"
            />

            <UEmpty
              v-else
              icon="i-hugeicons-checkmark-circle-02"
              title="No findings"
              description="The draft agent ran successfully and did not emit any findings or source telemetry."
              class="py-10"
            />
          </div>

          <UEmpty
            v-else-if="reviewerSectionsByLevel[item.level].length === 0"
            icon="i-hugeicons-search-remove"
            :title="`No ${item.label.toLowerCase()} findings`"
            description="This severity did not appear in the test result."
            class="py-10"
          />

          <div v-else class="grid gap-3">
            <template
              v-for="section in reviewerSectionsByLevel[item.level]"
              :key="`${item.level}:${section.reviewer.facet}:${section.reviewer.agent}`"
            >
              <article
                v-for="finding in section.findings"
                :key="`${finding.file}:${finding.line ?? 'no-line'}:${finding.summary}`"
                class="grid gap-2 rounded-md bg-default/70 p-3"
              >
                <p
                  class="min-w-0 break-words text-sm font-medium text-highlighted"
                >
                  {{ finding.summary }}
                </p>
                <p class="break-all font-mono text-xs text-muted">
                  {{ agentTestLocationLabel(finding) }}
                </p>
                <!-- Renders test findings the same way as live review findings, including fenced code. -->
                <Comark
                  :markdown="finding.body"
                  :plugins="markdownPlugins"
                  class="code-review-markdown-body text-sm text-default"
                />
              </article>
            </template>
          </div>
        </template>
      </UTabs>
    </div>
  </div>
</template>

<script setup lang="ts">
import { Comark } from "@comark/vue";
import {
  codeReviewAgentTestRunResultKindEnum,
  codeReviewFindingLevelEnum,
  type CodeReviewAgentTestRunResponse,
} from "@/api/generated";
import { useCodeHighlight } from "@/composables/useCodeHighlight";
import CodeReviewSourceTelemetryAccordion from "../shared/CodeReviewSourceTelemetryAccordion.vue";
import CodeReviewSummaryAccordion from "../shared/CodeReviewSummaryAccordion.vue";
import { hasSourceTelemetryContent as hasSourceTelemetryContentValue } from "../shared/source-telemetry-view-models";
import {
  agentTestLocationLabel,
  buildAgentTestSeverityTabs,
  buildAgentTestSummaryMetrics,
  buildReviewerSectionsByLevel,
  type AgentTestSeverityTab,
} from "./agent-test-view-models";

const props = defineProps<{
  result: CodeReviewAgentTestRunResponse | null;
}>();

const sourcesTabValue = "sources";

/**
 * Test output can include the same markdown and code fences as persisted review
 * output, so keep it on the shared Comark highlighting path.
 */
const { codeHighlightPlugins: markdownPlugins } = useCodeHighlight();

const summaryMetrics = computed(() => [
  ...buildAgentTestSummaryMetrics(props.result),
]);

/** Groups findings by severity while preserving reviewer facet context. */
const reviewerSectionsByLevel = computed(() =>
  buildReviewerSectionsByLevel(props.result),
);

const severityTabs = computed(() =>
  buildAgentTestSeverityTabs(reviewerSectionsByLevel.value),
);

const hasSourceTelemetryContent = computed(() =>
  hasSourceTelemetryContentValue(props.result?.findings.sourceTelemetry),
);

const resultTabs = computed<AgentTestSeverityTab[]>(() => {
  return [
    ...severityTabs.value,
    {
      label: "Summary",
      value: sourcesTabValue,
      level: codeReviewFindingLevelEnum.Critical,
      count: 0,
      color: "neutral",
      disabled: false,
    },
  ];
});

const defaultResultTab = computed(
  () =>
    severityTabs.value.find((item) => item.count > 0)?.value ?? sourcesTabValue,
);
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
