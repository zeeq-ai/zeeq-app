<template>
  <!--
  Editor for the repository-level shared prompt fragment. Injected into every
  reviewer agent's prompt (see CodeReviewPromptBuilder <organization_guidance>),
  so this is repository-wide guidance rather than a single agent's persona.
  -->
  <USlideover
    v-model:open="open"
    side="right"
    title="Shared prompt fragment"
    description="Guidance injected into every reviewer agent's prompt for this repository."
    :ui="{ content: 'w-200 sm:max-w-200' }"
  >
    <template #body>
      <div class="flex h-full min-h-96 min-w-96 flex-col gap-3">
        <div class="prompt-editor min-h-0 flex-1">
          <MdEditor
            v-model="draft"
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
      </div>
    </template>

    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          label="Cancel"
          color="neutral"
          variant="ghost"
          :disabled="saving"
          @click="cancel"
        />
        <UButton
          label="Save"
          icon="i-hugeicons-floppy-disk"
          color="neutral"
          variant="subtle"
          :loading="saving"
          :disabled="disabled || saving"
          @click="submit"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useColorMode } from "@vueuse/core";
import { MdEditor, type ToolbarNames } from "md-editor-v3";
import "md-editor-v3/lib/style.css";

const open = defineModel<boolean>("open", { required: true });

const props = defineProps<{
  sharedPromptFragment: string | null;
  saving: boolean;
  disabled: boolean;
}>();

const emits = defineEmits<{
  save: [sharedPromptFragment: string];
}>();

const draft = ref("");
const colorMode = useColorMode();

const editorTheme = computed<"light" | "dark">(() =>
  colorMode.value === "dark" ? "dark" : "light",
);

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

/** Reseeds the draft from the persisted value whenever the slideover (re)opens. */
watch(
  () => [open.value, props.sharedPromptFragment] as const,
  ([isOpen, sharedPromptFragment]) => {
    if (isOpen) {
      draft.value = sharedPromptFragment ?? "";
    }
  },
  { immediate: true },
);

function cancel() {
  open.value = false;
}

function submit() {
  if (props.disabled || props.saving) {
    return;
  }

  emits("save", draft.value);
}
</script>

<style scoped>
/* MdEditor owns its root element; keep sizing scoped to this editor instance. */
.prompt-editor :deep(.md-editor) {
  width: 100%;
  height: 100%;
  min-height: 24rem;
  box-sizing: border-box;
}
</style>
