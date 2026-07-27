<template>
  <!--
  Single editing surface for both create and update. The side panel carries
  metadata/actions so the prompt editor keeps the vertical space.
  -->
  <div class="flex h-full min-h-0 flex-1 overflow-hidden">
    <div class="flex min-w-0 flex-1 flex-col overflow-hidden">
      <div
        class="flex h-[45px] shrink-0 items-center gap-3 border-b border-default px-3"
      >
        <div class="flex min-w-0 flex-1 items-center gap-2">
          <h2 class="truncate text-xl font-bold text-highlighted">
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
        <UTooltip
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
      </div>

      <!-- Prompt and activation rules are tabs because both edit the same draft. -->
      <UTabs
        v-model="activeConfigurationTab"
        :items="agentConfigurationTabs"
        color="neutral"
        variant="link"
        class="agent-config-tabs min-h-0 flex-1 px-3"
        :ui="{ root: 'min-h-0 flex-1', content: 'min-h-0 flex-1 pt-3' }"
      >
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
        </template>

        <template #results>
          <AgentTestResultsPanel :result="agentTestResult" />
        </template>
      </UTabs>
    </div>

    <!--
    Mirrors the document editor side rail: identity fields, status, save/review,
    reset, copy, and destructive actions stay outside the editor canvas.
    -->
    <aside
      class="flex w-72 shrink-0 flex-col border-l border-default bg-default"
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

          <UFormField label="Display name" required>
            <UInput
              v-model="draft.displayName"
              placeholder="Structural reviewer"
              :disabled="disabled || saving"
              class="w-full"
            />
          </UFormField>

          <UFormField label="Facet" required>
            <UInput
              v-model="draft.reviewFacet"
              placeholder="Structural"
              :disabled="disabled || saving"
              class="w-full"
            />
          </UFormField>

          <UFormField label="Model tier" required>
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
import { computed, ref, watch } from "vue";
import { MdEditor, type ToolbarNames } from "md-editor-v3";
import "md-editor-v3/lib/style.css";
import type {
  CodeReviewAgentTestRunResponse,
  CodeReviewPullRequestDto,
  CodeReviewerActivationConfigurationDto,
  CodeReviewerAgentDto,
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
import { formatAgentFormForDiff } from "./agent-diff-format";

const props = withDefaults(
  defineProps<{
    agent: CodeReviewerAgentDto | null;
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
  },
);

const emits = defineEmits<{
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
}>();

const { editorTheme, toggleTheme } = useMarkdownEditorTheme();
const activeConfigurationTab = ref("prompt");
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

type AgentConfigurationTab = {
  label: string;
  icon: string;
  value: "prompt" | "filters" | "test" | "results";
  slot: "prompt" | "filters" | "test" | "results";
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
      icon: "i-hugeicons-chart-evaluation",
      value: "results",
      slot: "results" as const,
    });
  }

  return tabs;
});

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
watch(activeConfigurationTab, (tab) => {
  if (tab === "test" && props.agentTestTargets.length === 0) {
    emits("loadTestTargets");
  }
});

/**
 * A completed synthetic run should reveal its browser-local result immediately.
 * Subsequent runs replace the same result surface without becoming review history.
 */
watch(
  () => props.agentTestResult,
  (result) => {
    if (result) {
      activeConfigurationTab.value = "results";
      return;
    }

    if (activeConfigurationTab.value === "results") {
      // Results is conditional; return to the test workflow when its backing result is cleared.
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

defineExpose({ triggerSave, canSave, hasChanges });
</script>

<style scoped>
/* UTabs content must carry height through to the markdown editor. */
.agent-config-tabs :deep([data-slot="content"]) {
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
