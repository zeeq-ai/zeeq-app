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

    <div class="mt-4 flex flex-wrap items-center justify-between gap-2">
      <div class="min-w-0 flex-1">
        <div
          v-if="selectedPullRequest"
          class="inline-flex h-8 max-w-full items-center gap-2 rounded-md border border-default bg-elevated/30 px-2"
        >
          <UIcon
            name="i-hugeicons-git-pull-request"
            class="size-4 shrink-0 text-muted"
          />
          <span class="min-w-0 truncate text-sm text-highlighted">
            {{ selectedPullRequestLabel }}
          </span>
          <UButton
            icon="i-hugeicons-cancel-01"
            color="neutral"
            variant="ghost"
            size="xs"
            square
            aria-label="Clear selected pull request"
            :disabled="disabled || running"
            @click="clearSelectedPullRequest"
          />
        </div>
      </div>

      <div class="flex flex-wrap items-center justify-end gap-2">
        <UButton
          label="Refresh"
          icon="i-hugeicons-refresh"
          color="neutral"
          variant="ghost"
          size="md"
          :loading="loading"
          :disabled="disabled || running || loadingMore"
          @click="emits('loadTargets')"
        />
        <UButton
          v-if="hasMore"
          label="Load more"
          color="neutral"
          variant="ghost"
          size="md"
          :loading="loadingMore"
          :disabled="disabled || running || loading || loadingMore"
          @click="emits('loadMoreTargets')"
        />
        <UButton
          label="Run test"
          icon="i-hugeicons-play"
          color="neutral"
          variant="subtle"
          size="md"
          :loading="running"
          :disabled="disabled || running || !selectedPullRequest"
          @click="runSelected"
        />
      </div>
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
  agentTestTargetValue,
  buildAgentTestTargetRows,
  type AgentTestTargetRow,
} from "./agent-test-view-models";

const selectedPullRequest = defineModel<CodeReviewPullRequestDto | null>(
  "selectedPullRequest",
  { default: null },
);

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

/** View models keep listbox row rendering compact and avoid template-side formatting. */
const rows = computed<AgentTestTargetRow[]>(() =>
  buildAgentTestTargetRows(props.targets),
);

/** Bridges UListbox row objects to parent-owned PR selection by stable PR key. */
const selectedRow = computed<AgentTestTargetRow | undefined>({
  get: () => {
    if (!selectedPullRequest.value) {
      return undefined;
    }

    const selectedValue = agentTestTargetValue(selectedPullRequest.value);
    return rows.value.find((row) => row.value === selectedValue);
  },
  set: (row) => {
    selectedPullRequest.value = row?.pullRequest ?? null;
  },
});

const selectedPullRequestLabel = computed(() =>
  selectedPullRequest.value
    ? `#${selectedPullRequest.value.pullRequestNumber} ${selectedPullRequest.value.title}`
    : "",
);

function clearSelectedPullRequest() {
  selectedPullRequest.value = null;
}

function runSelected() {
  if (!selectedPullRequest.value) {
    return;
  }

  emits("run", selectedPullRequest.value);
}
</script>
