<template>
  <div class="flex min-h-0 flex-1 overflow-hidden">
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

      <UAlert
        class="mt-4"
        title="Common noise is already excluded"
        description="Lockfiles, build output, vendored dependencies, generated code, and editor/OS files (e.g. yarn.lock, node_modules/, bin/, obj/, DerivedData/, *.designer.cs) are filtered out of every repository's review scope automatically. The filters above narrow this repository's source files or override that default."
        icon="i-hugeicons-information-circle"
        color="neutral"
        variant="subtle"
      />
    </div>

    <!-- Preview evaluates the same unsaved draft rules currently visible in the editor. -->
    <aside
      class="flex w-80 shrink-0 flex-col border-l border-default bg-default p-4"
    >
      <div class="grid gap-4">
        <div class="grid gap-1">
          <h3 class="text-sm font-semibold text-highlighted">Test filters</h3>
          <p class="text-xs text-muted">
            Enter repository-relative file paths to see what review context
            keeps or filters out. Test up to 25 paths at a time.
          </p>
        </div>

        <UTextarea
          v-model="previewInput"
          :rows="10"
          autoresize
          placeholder="src/App.ts&#10;package-lock.json&#10;src/generated/client.ts"
          :disabled="disabled || previewLoading"
          class="w-full"
        />

        <p class="text-xs text-muted">
          {{ previewFilePaths.length }} / {{ maxPreviewFilePaths }} paths
        </p>

        <UButton
          label="Test filters"
          icon="i-hugeicons-play"
          color="neutral"
          variant="subtle"
          block
          :loading="previewLoading"
          :disabled="
            disabled ||
            previewLoading ||
            previewFilePaths.length === 0 ||
            tooManyPreviewFilePaths
          "
          :ui="{ base: 'justify-start' }"
          @click="previewDraft"
        />

        <UAlert
          v-if="previewMessage"
          :description="previewMessage"
          color="error"
          variant="subtle"
          icon="i-hugeicons-alert-02"
        />

        <div v-if="previewResult" class="grid min-h-0 gap-4">
          <section class="grid gap-2">
            <div class="flex items-center justify-between gap-2">
              <h4 class="text-xs font-semibold text-muted">Included</h4>
              <UBadge
                :label="previewResult.includedFiles.length"
                color="neutral"
                variant="subtle"
                size="sm"
                class="rounded-full"
              />
            </div>
            <div
              v-if="previewResult.includedFiles.length === 0"
              class="rounded-md border border-dashed border-default p-3 text-xs text-muted"
            >
              No paths.
            </div>
            <ul
              v-else
              class="max-h-48 space-y-1 overflow-y-auto rounded-md border border-default bg-muted/20 p-2"
            >
              <li
                v-for="path in previewResult.includedFiles"
                :key="path"
                class="break-all font-mono text-xs leading-5 text-success"
              >
                {{ path }}
              </li>
            </ul>
          </section>

          <section class="grid gap-2">
            <div class="flex items-center justify-between gap-2">
              <h4 class="text-xs font-semibold text-muted">Excluded</h4>
              <UBadge
                :label="previewResult.excludedFiles.length"
                color="neutral"
                variant="subtle"
                size="sm"
                class="rounded-full"
              />
            </div>
            <div
              v-if="previewResult.excludedFiles.length === 0"
              class="rounded-md border border-dashed border-default p-3 text-xs text-muted"
            >
              No paths.
            </div>
            <ul
              v-else
              class="max-h-48 space-y-1 overflow-y-auto rounded-md border border-default bg-muted/20 p-2"
            >
              <li
                v-for="path in previewResult.excludedFiles"
                :key="path"
                class="break-all font-mono text-xs leading-5 text-warning"
              >
                {{ path }}
              </li>
            </ul>
          </section>
        </div>
      </div>
    </aside>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import {
  codeReviewFileNameMatchTypeEnum,
  type CodeReviewFileFilterDto,
  type CodeReviewFileMatchCriteriaDto,
  type PreviewCodeReviewFileFilterResponse,
} from "@/api/generated";
import { cloneFileFilter, emptyFileFilter } from "@/stores/code-review-store";

import AgentActivationFiltersEditor from "./AgentActivationFiltersEditor.vue";
import { parsePreviewFilePaths } from "./file-filter-preview";

const props = defineProps<{
  fileFilter: CodeReviewFileFilterDto | null;
  saving: boolean;
  disabled: boolean;
  previewResult: PreviewCodeReviewFileFilterResponse | null;
  previewLoading: boolean;
  previewError: string | null;
}>();

const emits = defineEmits<{
  save: [fileFilter: CodeReviewFileFilterDto];
  preview: [fileFilter: CodeReviewFileFilterDto, filePaths: string[]];
}>();

type FilterPreset = {
  name: string;
  fileFilter: CodeReviewFileFilterDto;
};

const draft = ref<CodeReviewFileFilterDto>(emptyFileFilter());
const savedSnapshot = ref("");
const previewInput = ref(
  "src/App.ts\npackage-lock.json\nsrc/generated/client.ts",
);
const maxPreviewFilePaths = 25;

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
    name: "Kotlin",
    fileFilter: {
      includedFiles: [
        extension(".kt"),
        extension(".kts"),
        glob("*build.gradle*"),
        glob("*AndroidManifest.xml"),
        extension(".pro"),
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
const previewFilePaths = computed(() =>
  parsePreviewFilePaths(previewInput.value),
);
const tooManyPreviewFilePaths = computed(
  () => previewFilePaths.value.length > maxPreviewFilePaths,
);
const previewMessage = computed(() =>
  tooManyPreviewFilePaths.value
    ? `Enter ${maxPreviewFilePaths} or fewer file paths.`
    : props.previewError,
);

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

/** Sends the current unsaved draft rules with normalized user-entered paths. */
function previewDraft() {
  emits("preview", cloneFileFilter(draft.value), previewFilePaths.value);
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
