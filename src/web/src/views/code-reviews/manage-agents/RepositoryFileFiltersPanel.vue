<template>
  <div class="min-h-0 flex-1 overflow-y-auto px-6 py-5">
    <AgentActivationFiltersEditor
      :included-files="draft.includedFiles"
      :excluded-files="draft.excludedFiles"
      :disabled="disabled || saving"
      @update="updateDraft"
    />

    <div class="mt-6 flex flex-wrap items-center justify-between gap-3">
      <UFieldGroup>
        <UButton
          v-for="preset in filterPresets"
          :key="preset.name"
          :label="preset.name"
          color="neutral"
          variant="subtle"
          size="sm"
          :disabled="disabled || saving"
          @click="applyPreset(preset)"
        />
      </UFieldGroup>

      <div class="flex justify-end gap-2">
        <UButton
          label="Reset"
          color="neutral"
          variant="ghost"
          :disabled="disabled || saving || !dirty"
          @click="resetDraft"
        />
        <UButton
          label="Save filters"
          icon="i-hugeicons-floppy-disk"
          color="neutral"
          variant="subtle"
          :loading="saving"
          :disabled="!canSave"
          @click="emits('save', draft)"
        />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import {
  codeReviewFileNameMatchTypeEnum,
  type CodeReviewFileFilterDto,
  type CodeReviewFileMatchCriteriaDto,
} from "@/api/generated";
import { cloneFileFilter, emptyFileFilter } from "@/stores/code-review-store";

import AgentActivationFiltersEditor from "./AgentActivationFiltersEditor.vue";

const props = defineProps<{
  fileFilter: CodeReviewFileFilterDto | null;
  saving: boolean;
  disabled: boolean;
}>();

const emits = defineEmits<{
  save: [fileFilter: CodeReviewFileFilterDto];
}>();

type FilterPreset = {
  name: string;
  fileFilter: CodeReviewFileFilterDto;
};

const draft = ref<CodeReviewFileFilterDto>(emptyFileFilter());
const savedSnapshot = ref("");

const filterPresets: FilterPreset[] = [
  {
    name: "C#",
    fileFilter: {
      includedFiles: [
        extension(".cs"),
        extension(".razor"),
        glob("*appsettings*"),
        extension(".csproj"),
        extension(".cshtml"),
        extension(".css"),
        extension(".props"),
      ],
      excludedFiles: [
        glob("*schemas/*"),
        glob("*generated/*"),
        glob("*.designer.cs"),
        glob("*ModelSnapshot.cs"),
      ],
    },
  },
  {
    name: "TypeScript",
    fileFilter: {
      includedFiles: [
        extension(".ts"),
        extension(".tsx"),
        extension(".vue"),
        extension(".js"),
        extension(".jsx"),
        extension(".json"),
        extension(".css"),
      ],
      excludedFiles: [],
    },
  },
  {
    name: "Swift",
    fileFilter: {
      includedFiles: [
        extension(".swift"),
        extension(".xcodeproj"),
        extension(".xcworkspace"),
        extension(".plist"),
        extension(".storyboard"),
        extension(".xib"),
      ],
      excludedFiles: [],
    },
  },
  {
    name: "Dart",
    fileFilter: {
      includedFiles: [
        extension(".dart"),
        extension(".yaml"),
        extension(".yml"),
        extension(".json"),
      ],
      excludedFiles: [],
    },
  },
  {
    name: "F#",
    fileFilter: {
      includedFiles: [
        extension(".fs"),
        extension(".fsx"),
        extension(".fsi"),
        extension(".fsproj"),
        extension(".props"),
      ],
      excludedFiles: [],
    },
  },
];

/** Compares filter JSON after stable clone/reset boundaries. */
const dirty = computed(() => serialize(draft.value) !== savedSnapshot.value);
const canSave = computed(() => !props.disabled && !props.saving && dirty.value);

watch(
  () => props.fileFilter,
  (value) => {
    const next = cloneFileFilter(value);
    draft.value = next;
    savedSnapshot.value = serialize(next);
  },
  { immediate: true },
);

/** Applies include/exclude edits emitted by the shared rule editor. */
function updateDraft(value: CodeReviewFileFilterDto) {
  draft.value = cloneFileFilter(value);
}

/** Reverts unsaved edits to the last loaded repository configuration. */
function resetDraft() {
  draft.value = cloneFileFilter(props.fileFilter);
}

/** Applies a preset as an editable draft without changing the saved snapshot. */
function applyPreset(preset: FilterPreset) {
  draft.value = cloneFileFilter(preset.fileFilter);
}

function extension(pattern: string): CodeReviewFileMatchCriteriaDto {
  return {
    matchType: codeReviewFileNameMatchTypeEnum.Extension,
    pattern,
  };
}

function glob(pattern: string): CodeReviewFileMatchCriteriaDto {
  return {
    matchType: codeReviewFileNameMatchTypeEnum.Glob,
    pattern,
  };
}

function serialize(value: CodeReviewFileFilterDto): string {
  return JSON.stringify(value);
}
</script>
