<template>
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <div
      class="flex min-h-24 items-start justify-between gap-4 border-b border-default px-6 py-5"
    >
      <div class="min-w-0">
        <h2 class="truncate text-xl font-semibold text-highlighted">
          {{ agent ? "Edit reviewer agent" : "Create reviewer agent" }}
        </h2>
        <p class="mt-1 text-sm text-muted">
          Configure one reviewer persona and the files that activate it.
        </p>
      </div>
      <div class="flex shrink-0 items-center gap-2">
        <UTabs
          v-model="enabledTab"
          :items="enabledTabItems"
          :content="false"
          color="neutral"
          variant="pill"
          size="xs"
          :ui="{ list: 'w-auto', trigger: 'grow-0' }"
        />

        <UFieldGroup>
          <!--
        Copy (create mode only): opens the source library slideover where the
        user can seed this new agent from a built-in template or an existing
        agent in any org repository. The library emits a ready-to-use form.
        -->
          <UButton
            v-if="!agent"
            label="Templates"
            icon="i-hugeicons-copy-01"
            color="neutral"
            variant="subtle"
            :disabled="disabled || saving"
            @click="emits('openSourceLibrary')"
          />

          <UButton
            :label="agent ? 'Save' : 'Deploy'"
            icon="i-hugeicons-floppy-disk"
            color="neutral"
            variant="subtle"
            :loading="saving"
            :disabled="!canSave"
            @click="submit"
          />
          <UButton
            label="Cancel"
            color="neutral"
            variant="subtle"
            size="md"
            :disabled="saving"
            @click="emits('cancel')"
          />
        </UFieldGroup>
      </div>
    </div>

    <div class="flex min-h-0 flex-1 flex-col px-6 py-5">
      <div class="flex min-h-0 flex-1 flex-col gap-4">
        <div
          class="grid gap-3 lg:grid-cols-[minmax(0,2fr)_minmax(0,2fr)_minmax(10rem,1fr)]"
        >
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
        </div>

        <UTabs
          :items="agentConfigurationTabs"
          default-value="prompt"
          color="neutral"
          variant="link"
          class="agent-config-tabs min-h-0 flex-1"
          :ui="{ root: 'min-h-0 flex-1', content: 'min-h-0 flex-1 pt-4' }"
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
            <div class="h-full min-h-0 overflow-y-auto">
              <div class="grid gap-4">
                <div class="grid gap-2">
                  <h3 class="text-sm font-semibold text-highlighted">
                    Activation filters
                  </h3>
                  <p class="text-sm text-muted">
                    Empty includes mean this agent can activate for any
                    repository file that survives repository-level filters.
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
        </UTabs>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useColorMode } from "@vueuse/core";
import { MdEditor, type ToolbarNames } from "md-editor-v3";
import "md-editor-v3/lib/style.css";
import type {
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

import AgentActivationFiltersEditor from "./AgentActivationFiltersEditor.vue";

const props = defineProps<{
  agent: CodeReviewerAgentDto | null;
  saving: boolean;
  disabled: boolean;
  initialForm?: CodeReviewerAgentForm | null;
}>();

const emits = defineEmits<{
  cancel: [];
  save: [agentId: string | null, form: CodeReviewerAgentForm];
  openSourceLibrary: [];
}>();

const draft = ref<CodeReviewerAgentForm>(defaultAgentForm());
const colorMode = useColorMode();

const editorTheme = computed<"light" | "dark">(() =>
  colorMode.value === "dark" ? "dark" : "light",
);

const agentConfigurationTabs = [
  {
    label: "Prompt",
    value: "prompt",
    slot: "prompt" as const,
  },
  {
    label: "Activation filters",
    value: "filters",
    slot: "filters" as const,
  },
];

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
const canSave = computed(
  () =>
    !props.disabled &&
    !props.saving &&
    draft.value.displayName.trim().length > 0 &&
    draft.value.reviewFacet.trim().length > 0 &&
    draft.value.prompt.trim().length > 0,
);

watch(
  () => [props.agent, props.initialForm] as const,
  ([agent, initialForm]) => {
    if (agent) {
      draft.value = agentToForm(agent);
      return;
    }

    draft.value = initialForm ?? defaultAgentForm();
  },
  { immediate: true },
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

  emits("save", props.agent?.id ?? null, draft.value);
}
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
