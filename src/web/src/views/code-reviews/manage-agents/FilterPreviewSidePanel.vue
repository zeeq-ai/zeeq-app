<template>
  <aside
    class="flex shrink-0 flex-col border-l border-default bg-default p-4"
    :class="compact ? 'w-72' : 'w-80'"
  >
    <div class="grid gap-4">
      <div class="grid gap-1">
        <h3 class="text-sm font-semibold text-highlighted">Test filters</h3>
        <p class="text-xs text-muted">
          Enter repository-relative file paths to see what review context keeps
          or filters out. Test up to 25 paths at a time.
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
        @click="emits('preview', previewFilePaths)"
      />

      <UButton
        v-if="showSaveButton"
        label="Review and save"
        icon="i-hugeicons-floppy-disk"
        color="neutral"
        variant="subtle"
        block
        :loading="saving"
        :disabled="!canSave"
        :ui="{ base: 'justify-start' }"
        @click="emits('save')"
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
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import type { PreviewCodeReviewFileFilterResponse } from "@/api/generated";

import { parsePreviewFilePaths } from "./file-filter-preview";

const props = defineProps<{
  disabled: boolean;
  previewResult: PreviewCodeReviewFileFilterResponse | null;
  previewLoading: boolean;
  previewError: string | null;
  showSaveButton?: boolean;
  saving?: boolean;
  canSave?: boolean;
  compact?: boolean;
}>();

const emits = defineEmits<{
  preview: [filePaths: string[]];
  save: [];
}>();

const previewInput = ref(
  "src/App.ts\npackage-lock.json\nsrc/generated/client.ts",
);
const maxPreviewFilePaths = 25;

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
</script>
