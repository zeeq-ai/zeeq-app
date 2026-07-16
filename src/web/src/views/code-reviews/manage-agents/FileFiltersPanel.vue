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
          These filters apply before reviewer-agent activation for the currently
          selected repository. Excluded files always win.
        </p>
      </div>
    </div>

    <RepositoryFileFiltersPanel
      :file-filter
      :saving
      :disabled
      @save="emits('save', $event)"
    />
  </div>
</template>

<script setup lang="ts">
import type { CodeReviewFileFilterDto } from "@/api/generated";

import RepositoryFileFiltersPanel from "./RepositoryFileFiltersPanel.vue";

defineProps<{
  fileFilter: CodeReviewFileFilterDto | null;
  saving: boolean;
  disabled: boolean;
}>();

const emits = defineEmits<{
  save: [fileFilter: CodeReviewFileFilterDto];
}>();
</script>
