<template>
  <!--
  Back-testing target picker. The selected PR is local to this tab; the parent
  owns API calls so the component stays reusable and store-free.
  -->
  <div class="flex h-full min-h-0 flex-col overflow-hidden">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div class="grid gap-1">
        <h3 class="text-sm font-semibold text-highlighted">Test agent</h3>
        <p class="text-sm text-muted">
          Test the current configuration against an existing PR. Select a PR and
          click <b>Run test</b>.
        </p>
      </div>
    </div>

    <div class="mt-4 flex flex-wrap items-center justify-end gap-2">
      <UButton
        label="Refresh"
        icon="i-hugeicons-refresh"
        color="neutral"
        variant="ghost"
        size="md"
        :loading="loading"
        :disabled="disabled || running"
        @click="emits('loadTargets')"
      />
      <UButton
        v-if="hasMore"
        label="Load more"
        color="neutral"
        variant="ghost"
        size="md"
        :loading="loadingMore"
        :disabled="disabled || running || loadingMore"
        @click="emits('loadMoreTargets')"
      />
      <UButton
        label="Run test"
        icon="i-hugeicons-play"
        color="neutral"
        variant="subtle"
        size="md"
        :loading="running"
        :disabled="disabled || running || !selectedRow"
        @click="runSelected"
      />
    </div>

    <div class="my-3 min-h-0 flex-1 overflow-hidden">
      <template v-if="loading">
        <div class="grid gap-4">
          <USkeleton v-for="index in 5" :key="index" class="h-16 rounded-md" />
        </div>
      </template>

      <UListbox
        v-else-if="rows.length > 0"
        v-model="selectedRow"
        :items="rows"
        by="value"
        :filter="{
          placeholder: 'Filter by title, repo, or author...',
          icon: 'i-hugeicons-search-01',
        }"
        :filter-fields="['label', 'description', 'repo', 'authorLogin']"
        class="h-full min-h-0"
        :disabled="disabled || running"
        :ui="{
          root: 'ring-0 h-full min-h-0 rounded-md border border-default overflow-hidden',
          input: 'border-b border-default',
          content: 'max-h-none',
          group: 'p-0',
          item: 'px-3 py-2',
        }"
      >
        <template #item-leading="{ item }">
          <UIcon :name="item.icon" class="size-4" :class="item.iconClass" />
        </template>

        <template #item-label="{ item }">
          <div class="flex min-w-0 items-center gap-2">
            <span class="truncate text-sm font-medium text-highlighted">
              {{ item.label }}
            </span>
            <UBadge
              v-if="item.isDraft"
              label="Draft"
              color="warning"
              variant="subtle"
              size="sm"
              class="shrink-0 rounded-full"
            />
          </div>
        </template>

        <template #item-description="{ item }">
          <span class="text-xs text-muted">{{ item.description }}</span>
        </template>

        <template #item-trailing="{ item }">
          <UBadge
            :label="item.stateLabel"
            :color="item.stateColor"
            variant="subtle"
            size="sm"
            class="rounded-full"
          />
        </template>
      </UListbox>

      <UEmpty
        v-else
        icon="i-hugeicons-git-pull-request"
        title="No pull requests"
        description="No pull request records are available for this repository yet."
        class="py-16"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { CodeReviewPullRequestDto } from "@/api/generated";
import {
  buildAgentTestTargetRows,
  type AgentTestTargetRow,
} from "./agent-test-view-models";

const props = defineProps<{
  targets: CodeReviewPullRequestDto[];
  loading: boolean;
  loadingMore: boolean;
  running: boolean;
  disabled: boolean;
  hasMore: boolean;
}>();

const emits = defineEmits<{
  loadTargets: [];
  loadMoreTargets: [];
  run: [pullRequest: CodeReviewPullRequestDto];
}>();

const selectedRow = ref<AgentTestTargetRow | undefined>();

/** View models keep listbox row rendering compact and avoid template-side formatting. */
const rows = computed<AgentTestTargetRow[]>(() =>
  buildAgentTestTargetRows(props.targets),
);

/**
 * Preserve the user's selected PR while pages append, but choose the first row
 * on initial load so "Run test" has an obvious target.
 */
watch(
  rows,
  (nextRows) => {
    if (
      selectedRow.value &&
      nextRows.some((row) => row.value === selectedRow.value?.value)
    ) {
      return;
    }

    selectedRow.value = nextRows[0];
  },
  { immediate: true },
);

function runSelected() {
  if (!selectedRow.value) {
    return;
  }

  emits("run", selectedRow.value.pullRequest);
}
</script>
