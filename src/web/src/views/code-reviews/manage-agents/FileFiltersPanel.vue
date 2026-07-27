<template>
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <div
      class="flex min-h-24 items-start justify-between gap-4 border-b border-default px-6 py-5"
    >
      <div class="min-w-0">
        <h2 class="truncate text-xl font-semibold text-highlighted">
          Repo level file filters
        </h2>
        <p class="mt-1 text-sm text-muted">
          Configure file filters to reduce noise in code reviews for this
          repository. Agent level filters can also be configured separately.
        </p>
      </div>
    </div>

    <RepositoryFileFiltersPanel
      :file-filter
      :saving
      :disabled
      :preview-result
      :preview-loading
      :preview-error
      @save="emits('save', $event)"
      @preview="previewFilters"
    />
  </div>
</template>

<script setup lang="ts">
import type {
  CodeReviewFileFilterDto,
  PreviewCodeReviewFileFilterResponse,
} from "@/api/generated";

import RepositoryFileFiltersPanel from "./RepositoryFileFiltersPanel.vue";

defineProps<{
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

function previewFilters(
  fileFilter: CodeReviewFileFilterDto,
  filePaths: string[],
) {
  emits("preview", fileFilter, filePaths);
}
</script>
