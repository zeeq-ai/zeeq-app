<template>
  <UPageCard variant="subtle" :ui="{ container: 'p-4 sm:p-4' }">
    <div class="grid gap-3 lg:grid-cols-[minmax(0,1fr)_20rem] lg:items-end">
      <div>
        <h2 class="text-base font-semibold text-highlighted">Repository</h2>
        <p class="mt-1 text-sm text-muted">
          Reviewer agents and file filters are scoped to one configured GitHub
          repository.
        </p>
      </div>

      <USelect
        :model-value="selectedRepositoryId ?? undefined"
        :items="repositoryItems"
        color="neutral"
        :disabled="loading || repositories.length === 0"
        class="w-full"
        @update:model-value="emits('select', $event)"
      />
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import type { GitHubConfiguredRepository } from "@/stores/github-settings-store";

const props = defineProps<{
  repositories: GitHubConfiguredRepository[];
  selectedRepositoryId: string | null;
  loading: boolean;
}>();

const emits = defineEmits<{
  select: [repositoryId: string];
}>();

/** Select items use display names while preserving the local Zeeq repository id. */
const repositoryItems = computed(() =>
  props.repositories.map((repository) => ({
    label: repository.displayName,
    value: repository.id,
  })),
);
</script>
