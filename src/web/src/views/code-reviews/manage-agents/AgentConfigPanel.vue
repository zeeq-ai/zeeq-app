<template>
  <!--
  Single editing surface for both create and update. The side panel carries
  metadata/actions so the prompt editor keeps the vertical space.
  -->
  <div class="flex h-full min-h-0 flex-1 flex-col overflow-hidden sm:flex-row">
    <div class="flex min-w-0 flex-1 flex-col overflow-hidden">
      <div
        class="flex h-[45px] shrink-0 items-center gap-3 border-b border-default px-3"
      >
        <div class="flex min-w-0 flex-1 items-center gap-2">
          <USelectMenu
            v-if="isRunWorkspace && compactWorkspace && agent"
            :model-value="agent.id"
            :items="reviewerItems"
            value-key="value"
            variant="ghost"
            color="neutral"
            aria-label="Reviewer"
            class="min-w-0 max-w-full text-lg font-semibold"
            @update:model-value="(id) => id && emits('selectReviewer', id)"
          />
          <h2 v-else class="truncate text-xl font-bold text-highlighted">
            {{
              draft.displayName.trim() ||
              (agent ? "Reviewer agent" : "New reviewer agent")
            }}
          </h2>
          <UBadge
            v-if="!agent"
            label="New"
            color="primary"
            variant="subtle"
            size="sm"
            class="shrink-0 rounded-full"
          />
        </div>
        <UBadge
          v-if="hasChanges"
          label="Unsaved"
          color="warning"
          variant="subtle"
          size="sm"
          class="rounded-full"
        />
        <div class="flex shrink-0 items-center gap-1">
          <UTooltip
            v-if="activeConfigurationTab === 'prompt'"
            text="Toggle editor theme"
            :content="{ side: 'bottom' }"
            :delay-duration="0"
          >
            <UButton
              icon="i-hugeicons-gibbous-moon"
              size="xs"
              color="neutral"
              variant="ghost"
              aria-label="Toggle editor theme"
              @click="toggleTheme"
            />
          </UTooltip>
          <UTooltip
            :text="rightRailToggleLabel"
            :content="{ side: 'bottom' }"
            :delay-duration="0"
          >
            <UButton
              :icon="
                rightRailOpen
                  ? 'i-hugeicons-panel-right-close'
                  : 'i-hugeicons-panel-right-open'
              "
              size="xs"
              color="neutral"
              variant="ghost"
              :aria-label="rightRailToggleLabel"
              :aria-expanded="rightRailOpen"
              :aria-controls="rightRailId"
              @click="rightRailOpen = !rightRailOpen"
            />
          </UTooltip>
        </div>
      </div>

      <UTabs
        v-model="activeConfigurationTab"
        :items="agentConfigurationTabs"
        :unmount-on-hide="false"
        color="neutral"
        variant="link"
        class="agent-config-tabs min-h-0 flex-1 px-3"
        :ui="{
          root: 'min-h-0 flex-1',
          list: 'shrink-0 overflow-x-auto',
          trigger: 'shrink-0',
          content: 'min-h-0 flex-1 pt-3',
        }"
      >
        <template #default="{ item }">
          <span class="inline-flex shrink-0 items-center gap-1.5">
            <span class="whitespace-nowrap">{{ item.label }}</span>
            <UBadge
              v-if="item.value === 'filters'"
              :label="`${activationFilterCount}`"
              color="neutral"
              variant="soft"
              size="sm"
              square
            />
          </span>
        </template>

        <template #prompt>
          <div class="prompt-editor min-h-0 flex-1">
            <MdEditor
              v-model="draft.prompt"
              preview-theme="github"
              language="en-US"
              :preview="false"
              :toolbars-exclude="promptToolbarsExclude"
              :html-preview="false"
              :no-upload-img="true"
              :no-mermaid="true"
              :no-katex="true"
              :theme="editorTheme"
              :disabled="disabled || saving"
            />
          </div>
        </template>

        <template #filters>
          <div class="h-full min-h-0 overflow-y-auto pb-3">
            <div class="grid gap-4">
              <div class="grid gap-2">
                <h3 class="text-sm font-semibold text-highlighted">
                  Activation filters
                </h3>
                <p class="text-sm text-muted">
                  Empty includes mean this agent can activate for any repository
                  file that survives repository-level filters.
                </p>
              </div>

              <AgentActivationFiltersEditor
                :included-files="draft.activationConfiguration.includedFiles"
                :excluded-files="draft.activationConfiguration.excludedFiles"
                :disabled="disabled || saving"
                @update="updateActivationConfiguration"
              />
            </div>
          </div>
        </template>

        <template #test>
          <AgentTestPanel
            v-if="repositoryId"
            v-model:selected-pull-request="selectedAgentTestPullRequest"
            :targets="agentTestTargets"
            :loading="agentTestTargetsLoading"
            :loading-more="agentTestTargetsLoadingMore"
            :running="agentTestRunning"
            :disabled="disabled || saving"
            :has-more="agentTestTargetsHasMore"
            @load-targets="emits('loadTestTargets')"
            @load-more-targets="emits('loadMoreTestTargets')"
            @run="runAgentTest"
          />
          <UEmpty
            v-else
            title="Select a repository"
            description="Choose a repository to test this reviewer."
          />
        </template>

        <template #results>
          <AgentTestResultsPanel :result="agentTestResult" />
        </template>

        <template #evals>
          <EvaluationWorkspace
            v-if="repositoryId && hasOpenedEvals"
            :key="repositoryId"
            ref="evaluationWorkspace"
            :repository-id="repositoryId"
            :repository-name="repositoryName"
            :agent-id="agent?.id ?? null"
            :reviewers="reviewers"
            :reviewer-configuration="reviewerConfiguration"
            :can-manage="!disabled && !saving"
          />
          <UEmpty
            v-else-if="!repositoryId"
            title="Select a repository"
            description="Choose a repository to manage evaluation datasets and runs."
          />
        </template>
      </UTabs>
    </div>

    <!--
    Mirrors the document editor side rail: identity fields, status, save/review,
    reset, copy, and destructive actions stay outside the editor canvas.
    -->
    <FilterPreviewSidePanel
      v-if="activeConfigurationTab === 'filters'"
      v-show="rightRailOpen"
      :id="rightRailId"
      class="min-h-0 overflow-y-auto max-sm:max-h-[45%] max-sm:w-full max-sm:border-t max-sm:border-l-0"
      :disabled="disabled || saving"
      :preview-result="activationFilterPreviewResult"
      :preview-loading="activationFilterPreviewLoading"
      :preview-error="activationFilterPreviewError"
      :show-save-button="true"
      :saving
      :can-save="canSave"
      compact
      @preview="previewActivationFilters"
      @save="submit"
    />

    <aside
      v-else
      v-show="rightRailOpen"
      :id="rightRailId"
      class="flex min-h-0 max-h-[45%] shrink-0 flex-col border-t border-default bg-default sm:max-h-none sm:w-72 sm:border-t-0 sm:border-l"
    >
      <div class="min-h-0 flex-1 overflow-y-auto p-4">
        <div class="grid gap-4">
          <div class="grid gap-1">
            <h3 class="text-sm font-semibold text-highlighted">
              Reviewer settings
            </h3>
            <p class="text-xs text-muted">
              Configure one reviewer persona and the files that activate it.
            </p>
          </div>

          <UFormField
            label="Display name"
            description="A human-readable display name; a persona for the agent."
            required
          >
            <UInput
              v-model="draft.displayName"
              placeholder="Structural reviewer"
              :disabled="disabled || saving"
              class="w-full"
            />
          </UFormField>

          <UFormField
            label="Facet"
            description="The short name of the review facet rendered in results."
            required
          >
            <UInput
              v-model="draft.reviewFacet"
              placeholder="Structural"
              :disabled="disabled || saving"
              class="w-full"
            />
          </UFormField>

          <UFormField
            label="Model tier"
            description="Maps to an underlying LLM model configured in settings."
            required
          >
            <USelect
              v-model="draft.modelTier"
              :items="modelTierItems"
              color="neutral"
              :disabled="disabled || saving"
              class="w-full"
            />
          </UFormField>

          <UFormField label="Status">
            <UTabs
              v-model="enabledTab"
              :items="enabledTabItems"
              :content="false"
              color="neutral"
              variant="pill"
              size="xs"
              class="w-full"
              :ui="{ list: 'w-full', trigger: 'flex-1' }"
            />
          </UFormField>

          <USeparator />

          <UButton
            :label="agent ? 'Review and save' : 'Deploy'"
            icon="i-hugeicons-floppy-disk"
            color="neutral"
            variant="subtle"
            block
            :loading="saving"
            :disabled="!canSave"
            :ui="sidePanelButtonUi"
            @click="submit"
          />
          <UButton
            label="Reset changes"
            icon="i-hugeicons-arrow-reload-horizontal"
            color="neutral"
            variant="ghost"
            block
            :disabled="saving || !hasChanges"
            :ui="sidePanelButtonUi"
            @click="resetDraftToSaved"
          />

          <template v-if="!agent">
            <UButton
              label="Templates"
              icon="i-hugeicons-copy-01"
              color="neutral"
              variant="ghost"
              block
              :disabled="disabled || saving"
              :ui="sidePanelButtonUi"
              @click="emits('openSourceLibrary')"
            />
            <UButton
              label="Cancel"
              color="neutral"
              variant="ghost"
              block
              :disabled="saving"
              :ui="sidePanelButtonUi"
              @click="emits('cancel')"
            />
          </template>

          <template v-else>
            <USeparator />

            <USelect
              v-if="copyTargetRepositoryItems.length > 0"
              v-model="copyTargetRepositoryId"
              :items="copyTargetRepositoryItems"
              placeholder="Copy to repository"
              color="neutral"
              :disabled="disabled || saving"
              class="w-full"
              @update:model-value="copyToRepository"
            />
          </template>
        </div>
      </div>

      <div v-if="agent" class="grid gap-2 border-t border-default p-4">
        <ZeeqPopConfirm
          title="Delete reviewer agent?"
          :body="`Delete ${draft.displayName || 'this reviewer agent'} from this repository's reviewer agents?`"
          confirm-label="Delete"
          label="Delete"
          icon="i-hugeicons-delete-02"
          color="error"
          variant="ghost"
          block
          :disabled="disabled || saving"
          :ui="sidePanelButtonUi"
          @confirm="emits('delete')"
        />
      </div>
    </aside>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, useId, watch } from "vue";
import { useRoute } from "vue-router";
import { useMediaQuery } from "@vueuse/core";
import { MdEditor, type ToolbarNames } from "md-editor-v3";
import "md-editor-v3/lib/style.css";
import type {
  CodeReviewAgentTestRunResponse,
  PreviewCodeReviewFileFilterResponse,
  CodeReviewPullRequestDto,
  CodeReviewerActivationConfigurationDto,
  CodeReviewerAgentDto,
  CodeReviewerAgentRevisionConfiguration,
} from "@/api/generated";
import {
  agentToForm,
  cloneActivationConfiguration,
  defaultAgentForm,
  modelTierItems,
  type CodeReviewerAgentForm,
} from "@/stores/code-review-store";
import { useDraftSnapshot } from "@/composables/useDraftSnapshot";
import { useMarkdownEditorTheme } from "@/composables/useMarkdownEditorTheme";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";

import AgentActivationFiltersEditor from "./AgentActivationFiltersEditor.vue";
import AgentTestPanel from "./AgentTestPanel.vue";
import AgentTestResultsPanel from "./AgentTestResultsPanel.vue";
import FilterPreviewSidePanel from "./FilterPreviewSidePanel.vue";
import EvaluationWorkspace from "./EvaluationWorkspace.vue";
import { formatAgentFormForDiff } from "./agent-diff-format";

const props = withDefaults(
  defineProps<{
    agent: CodeReviewerAgentDto | null;
    repositoryId?: string;
    repositoryName?: string;
    reviewers?: CodeReviewerAgentDto[];
    saving: boolean;
    disabled: boolean;
    initialForm?: CodeReviewerAgentForm | null;
    copyTargetRepositoryItems?: { label: string; value: string }[];
    agentTestTargets?: CodeReviewPullRequestDto[];
    agentTestTargetsLoading?: boolean;
    agentTestTargetsLoadingMore?: boolean;
    agentTestTargetsHasMore?: boolean;
    agentTestRunning?: boolean;
    agentTestResult?: CodeReviewAgentTestRunResponse | null;
    activationFilterPreviewResult?: PreviewCodeReviewFileFilterResponse | null;
    activationFilterPreviewLoading?: boolean;
    activationFilterPreviewError?: string | null;
  }>(),
  {
    initialForm: null,
    copyTargetRepositoryItems: () => [],
    agentTestTargets: () => [],
    agentTestTargetsLoading: false,
    agentTestTargetsLoadingMore: false,
    agentTestTargetsHasMore: false,
    agentTestRunning: false,
    agentTestResult: null,
    repositoryId: "",
    repositoryName: "Repository",
    reviewers: () => [],
    activationFilterPreviewResult: null,
    activationFilterPreviewLoading: false,
    activationFilterPreviewError: null,
  },
);

const emits = defineEmits<{
  workspaceMode: [isRunWorkspace: boolean];
  selectReviewer: [agentId: string];
  cancel: [];
  save: [agentId: string | null, form: CodeReviewerAgentForm];
  review: [
    agentId: string,
    original: string,
    next: string,
    form: CodeReviewerAgentForm,
  ];
  delete: [];
  copyToRepository: [repositoryId: string];
  openSourceLibrary: [];
  loadTestTargets: [];
  loadMoreTestTargets: [];
  runTest: [pullRequest: CodeReviewPullRequestDto, form: CodeReviewerAgentForm];
  previewActivationFilters: [
    activationConfiguration: CodeReviewerActivationConfigurationDto,
    filePaths: string[],
  ];
}>();

const { editorTheme, toggleTheme } = useMarkdownEditorTheme();
const route = useRoute();
const linkedEvaluationCase = computed(() => {
  const { repositoryId, datasetId, prNumber } = route.query;
  if (
    !props.repositoryId ||
    props.repositoryId !== repositoryId ||
    typeof datasetId !== "string" ||
    typeof prNumber !== "string"
  ) {
    return null;
  }
  const pullRequestNumber = Number(prNumber);
  return Number.isInteger(pullRequestNumber) && pullRequestNumber > 0
    ? { datasetId, pullRequestNumber }
    : null;
});
const activeConfigurationTab = ref(
  linkedEvaluationCase.value ? "evals" : props.agent ? "test" : "prompt",
);
const rightRailId = useId();
const editorRailOpen = ref(true);
const evalsRailOpen = ref(false);
const rightRailOpen = computed({
  get: () =>
    activeConfigurationTab.value === "evals"
      ? evalsRailOpen.value
      : editorRailOpen.value,
  set: (open) => {
    if (activeConfigurationTab.value === "evals") {
      evalsRailOpen.value = open;
    } else {
      editorRailOpen.value = open;
    }
  },
});
const rightRailToggleLabel = computed(() => {
  const panel =
    activeConfigurationTab.value === "filters"
      ? "filter preview"
      : "reviewer settings";
  return rightRailOpen.value ? `Hide ${panel}` : `Show ${panel}`;
});

const isRunWorkspace = computed(
  () =>
    activeConfigurationTab.value === "test" ||
    activeConfigurationTab.value === "results" ||
    activeConfigurationTab.value === "evals",
);
const selectedAgentTestPullRequest = ref<CodeReviewPullRequestDto | null>(null);
// Keep an opened evaluation workspace mounted so tab switches preserve drafts.
const hasOpenedEvals = ref(false);
const evaluationWorkspace = ref<InstanceType<
  typeof EvaluationWorkspace
> | null>(null);
const compactWorkspace = useMediaQuery("(max-width: 1439px)");
const reviewerItems = computed(() =>
  props.reviewers.map((reviewer) => ({
    label: reviewer.displayName,
    value: reviewer.id,
  })),
);
watch(isRunWorkspace, (active) => emits("workspaceMode", active), {
  immediate: true,
});
const copyTargetRepositoryId = ref<string | undefined>(undefined);
const originalDiffText = ref(formatAgentFormForDiff(defaultAgentForm()));
const {
  draft,
  dirty: formDirty,
  resetDraft,
  resetToBaseline,
} = useDraftSnapshot(defaultAgentForm(), {
  clone: cloneAgentForm,
  serialize: serializeAgentForm,
});

type AgentConfigurationTabName =
  "prompt" | "filters" | "test" | "results" | "evals";
type AgentConfigurationTab = {
  label: string;
  icon: string;
  value: AgentConfigurationTabName;
  slot: AgentConfigurationTabName;
};

const agentConfigurationTabs = computed<AgentConfigurationTab[]>(() => {
  const tabs: AgentConfigurationTab[] = [
    {
      label: "Prompt",
      icon: "i-hugeicons-ai-programming",
      value: "prompt",
      slot: "prompt" as const,
    },
    {
      label: "Activation filters",
      icon: "i-hugeicons-filter-edit",
      value: "filters",
      slot: "filters" as const,
    },
    {
      label: "Test",
      icon: "i-hugeicons-test-tube-01",
      value: "test",
      slot: "test" as const,
    },
  ];

  if (props.agentTestResult) {
    tabs.push({
      label: "Results",
      icon: "i-hugeicons-message-programming",
      value: "results",
      slot: "results",
    });
  }

  tabs.push({
    label: "Evals",
    icon: "i-hugeicons-chart-evaluation",
    value: "evals",
    slot: "evals",
  });

  return tabs;
});

const reviewerConfiguration = computed<CodeReviewerAgentRevisionConfiguration>(
  () => ({
    displayName: draft.value.displayName,
    reviewFacet: draft.value.reviewFacet,
    modelTier: draft.value.modelTier,
    prompt: draft.value.prompt,
    enabled: draft.value.enabled,
    activationConfiguration: {
      includedFiles: draft.value.activationConfiguration.includedFiles.map(
        (item) => ({ ...item }),
      ),
      excludedFiles: draft.value.activationConfiguration.excludedFiles.map(
        (item) => ({ ...item }),
      ),
    },
  }),
);

const enabledTabItems = computed(() => [
  {
    label: "Enabled",
    value: "enabled",
    disabled: props.disabled || props.saving,
  },
  {
    label: "Disabled",
    value: "disabled",
    disabled: props.disabled || props.saving,
  },
]);

const enabledTab = computed({
  get: () => (draft.value.enabled ? "enabled" : "disabled"),
  set: (value: string | number) => {
    draft.value.enabled = value === "enabled";
  },
});

const promptToolbarsExclude: ToolbarNames[] = [
  "save",
  "catalog",
  "image",
  "github",
  "htmlPreview",
  "pageFullscreen",
  "fullscreen",
  "mermaid",
  "katex",
  "prettier",
];

/** Required fields mirror backend validation before submitting a mutation. */
const formValid = computed(
  () =>
    !props.disabled &&
    !props.saving &&
    draft.value.displayName.trim().length > 0 &&
    draft.value.reviewFacet.trim().length > 0 &&
    draft.value.prompt.trim().length > 0,
);

const canSave = computed(
  () => formValid.value && (!props.agent || formDirty.value),
);
const hasChanges = computed(() => formDirty.value);
const activationFilterCount = computed(
  () =>
    draft.value.activationConfiguration.includedFiles.length +
    draft.value.activationConfiguration.excludedFiles.length,
);
const sidePanelButtonUi = { base: "justify-start" };

watch(
  () => [props.agent, props.initialForm] as const,
  ([agent, initialForm]) => {
    const next = agent
      ? agentToForm(agent)
      : initialForm
        ? cloneAgentForm(initialForm)
        : defaultAgentForm();

    // Baseline reset defines both dirty tracking and the "original" side of the diff.
    resetToBaseline(next);
    originalDiffText.value = formatAgentFormForDiff(next);

    if (agent) {
      return;
    }

    copyTargetRepositoryId.value = undefined;
  },
  { immediate: true },
);

/**
 * Test targets load lazily because the editor opens far more often than the
 * back-testing flow. The root view owns the store call; this panel only signals
 * intent when the tab becomes visible.
 */
watch(
  [activeConfigurationTab, () => props.repositoryId],
  ([tab, repositoryId], previous) => {
    if (previous && previous[1] !== repositoryId) {
      selectedAgentTestPullRequest.value = null;
    }
    if (tab === "test" && repositoryId && props.agentTestTargets.length === 0) {
      emits("loadTestTargets");
    }
    if (tab === "evals") {
      hasOpenedEvals.value = true;
    }
  },
  { immediate: true },
);

watch(linkedEvaluationCase, (linkedCase) => {
  if (linkedCase) {
    activeConfigurationTab.value = "evals";
  }
});

/**
 * A completed synthetic run should reveal its browser-local result immediately.
 * Subsequent runs replace the same result surface without becoming review history.
 */
watch(
  () => props.agentTestResult,
  (result) => {
    if (
      result &&
      activeConfigurationTab.value !== "evals" &&
      !linkedEvaluationCase.value
    ) {
      activeConfigurationTab.value = "results";
    } else if (!result && activeConfigurationTab.value === "results") {
      activeConfigurationTab.value = "test";
    }
  },
);

/** Replaces activation rules with a cloned value from the shared rule editor. */
function updateActivationConfiguration(
  value: CodeReviewerActivationConfigurationDto,
) {
  draft.value = {
    ...draft.value,
    activationConfiguration: cloneActivationConfiguration(value),
  };
}

/** Sends the current unsaved activation rules with normalized preview paths. */
function previewActivationFilters(filePaths: string[]) {
  emits(
    "previewActivationFilters",
    cloneActivationConfiguration(draft.value.activationConfiguration),
    filePaths,
  );
}

function submit() {
  if (!canSave.value) {
    return;
  }

  const form = cloneAgentForm(draft.value);

  if (props.agent) {
    // Existing agents review through the diff drawer; new agents deploy directly.
    emits(
      "review",
      props.agent.id,
      originalDiffText.value,
      formatAgentFormForDiff(form),
      form,
    );
    return;
  }

  emits("save", null, form);
}

function triggerSave() {
  if (!canSave.value) {
    return;
  }

  submit();
}

function resetDraftToSaved() {
  resetDraft();
}

function copyToRepository(repositoryId: string) {
  copyTargetRepositoryId.value = undefined;
  emits("copyToRepository", repositoryId);
}

function runAgentTest(pullRequest: CodeReviewPullRequestDto) {
  emits("runTest", pullRequest, cloneAgentForm(draft.value));
}

function cloneAgentForm(form: CodeReviewerAgentForm): CodeReviewerAgentForm {
  return {
    ...form,
    activationConfiguration: cloneActivationConfiguration(
      form.activationConfiguration,
    ),
  };
}

function serializeAgentForm(form: CodeReviewerAgentForm): string {
  return JSON.stringify(form);
}

function canLeaveDatasetWorkspace() {
  return evaluationWorkspace.value?.canLeaveDatasetWorkspace() ?? true;
}

defineExpose({ triggerSave, canSave, hasChanges, canLeaveDatasetWorkspace });
</script>

<style scoped>
/* UTabs content must carry height through to the markdown editor. */
.agent-config-tabs :deep([data-slot="content"]:not([hidden])) {
  display: flex;
  flex-direction: column;
}

/* MdEditor owns its root element; keep sizing scoped to that root. */
.prompt-editor :deep(.md-editor) {
  width: 100%;
  height: 100%;
  min-height: 0;
  box-sizing: border-box;
}
</style>
