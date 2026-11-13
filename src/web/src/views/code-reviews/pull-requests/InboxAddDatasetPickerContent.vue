<template>
  <section class="flex h-full min-h-0 flex-col overflow-hidden bg-default">
    <header
      class="flex min-h-14 items-center gap-3 border-b border-default px-4 py-3"
    >
      <UButton
        v-if="embedded"
        :label="`← Back to PR #${pullRequest.pullRequestNumber}`"
        color="neutral"
        variant="ghost"
        size="sm"
        class="shrink-0"
        @click="emits('back')"
      />
      <div v-else class="min-w-0 flex-1">
        <h2 class="truncate text-base font-semibold text-highlighted">
          Add to dataset
        </h2>
        <UTooltip :text="description" :delay-duration="0">
          <p class="truncate text-sm text-muted">{{ description }}</p>
        </UTooltip>
      </div>
      <h2
        v-if="embedded"
        class="min-w-0 truncate text-base font-semibold text-highlighted"
      >
        Add to dataset
      </h2>
      <UButton
        v-if="embedded"
        icon="i-hugeicons-cancel-01"
        aria-label="Close pull request details"
        color="neutral"
        variant="ghost"
        size="sm"
        square
        class="ms-auto shrink-0"
        @click="emits('cancel')"
      />
      <UButton
        v-else
        icon="i-hugeicons-cancel-01"
        aria-label="Close Add to dataset"
        color="neutral"
        variant="ghost"
        size="sm"
        square
        @click="emits('cancel')"
      />
    </header>

    <main class="min-h-0 flex-1 space-y-4 overflow-y-auto p-4">
      <div class="rounded-md border border-default bg-elevated/40 p-3">
        <div class="flex items-center justify-between gap-3">
          <div class="min-w-0 truncate text-sm font-medium text-highlighted">
            #{{ pullRequest.pullRequestNumber }} {{ pullRequest.title }}
          </div>
          <UBadge
            :label="pullRequest.state"
            :color="pullRequest.state === 'Open' ? 'success' : 'neutral'"
            variant="subtle"
            class="shrink-0"
          />
        </div>
        <p class="mt-1 text-xs text-muted">
          Current PR head · Captured when you add.
        </p>
      </div>

      <UInput
        :model-value="search"
        icon="i-hugeicons-search-01"
        placeholder="Search datasets"
        aria-label="Search datasets"
        size="sm"
        class="w-full"
        @update:model-value="emits('update:search', $event)"
      />

      <div v-if="loadingDatasets" class="grid gap-2">
        <USkeleton v-for="index in 3" :key="index" class="h-11 rounded-md" />
      </div>
      <UEmpty
        v-else-if="datasets.length === 0"
        icon="i-hugeicons-database-02"
        title="No datasets for this repository"
        description="Create a dataset to save this pull request for evaluation."
        size="sm"
      />
      <fieldset v-else class="min-w-0 space-y-1" aria-label="Choose a dataset">
        <legend class="sr-only">Choose a dataset</legend>
        <div
          v-for="dataset in datasets"
          :key="dataset.id"
          class="min-w-0 rounded-md border px-2 transition-colors"
          :class="[
            selectedDatasetId === dataset.id &&
            containsSelectedPullRequest(dataset)
              ? 'border-default bg-elevated/40 ring-1 ring-default'
              : selectedDatasetId === dataset.id
                ? 'border-primary ring-1 ring-primary/30'
                : 'border-default',
            datasetDisabled(dataset) && !containsSelectedPullRequest(dataset)
              ? 'opacity-60'
              : 'hover:bg-elevated/50',
          ]"
        >
          <div class="flex h-11 min-w-0 items-center gap-2">
            <label
              class="flex min-w-0 flex-1 cursor-pointer items-center gap-2"
            >
              <input
                type="radio"
                name="inbox-add-dataset"
                class="peer sr-only"
                :value="dataset.id"
                :checked="selectedDatasetId === dataset.id"
                :disabled="datasetDisabled(dataset)"
                :aria-label="`Select ${dataset.name}`"
                @change="emits('select', dataset.id)"
              />
              <span
                class="size-4 shrink-0 rounded-full border border-muted peer-checked:border-primary peer-checked:bg-primary peer-focus-visible:ring-2 peer-focus-visible:ring-primary peer-focus-visible:ring-offset-2"
                aria-hidden="true"
              />
              <span class="min-w-0 flex-1 truncate text-sm text-highlighted">
                {{ dataset.name }}
              </span>
              <span
                class="shrink-0 whitespace-nowrap font-mono text-xs tabular-nums text-muted"
              >
                v{{ versionFor(dataset) }} · {{ Number(dataset.caseCount) }}/{{
                  maxCases
                }}
              </span>
            </label>
            <span
              v-if="selectedDatasetId === dataset.id && loadingSelectedDataset"
              class="size-3 shrink-0 animate-spin rounded-full border-2 border-muted border-r-transparent"
              role="status"
              aria-label="Checking dataset membership"
            />
            <span
              v-else-if="
                Number(dataset.caseCount) >= maxCases &&
                !containsSelectedPullRequest(dataset)
              "
              class="shrink-0 whitespace-nowrap text-xs text-muted"
            >
              {{ maxCases }}/{{ maxCases }} full
            </span>
          </div>
          <div
            v-if="containsSelectedPullRequest(dataset)"
            class="flex min-h-8 items-center justify-end gap-3 pb-1 ps-6"
          >
            <span class="whitespace-nowrap text-xs text-muted">
              Contains #{{ pullRequest.pullRequestNumber }}
            </span>
            <UButton
              v-if="canOpenEditor"
              label="Open case"
              color="primary"
              variant="link"
              size="xs"
              class="shrink-0"
              :disabled="adding || creatingDataset"
              @click="emits('open-editor', dataset.id, reviewerIds)"
            />
          </div>
        </div>
      </fieldset>

      <div v-if="creatingNew" class="space-y-3">
        <UFormField label="Dataset name" name="dataset-name" required>
          <UInput
            :model-value="newDatasetName"
            placeholder="e.g. Core reviewer checks"
            size="sm"
            class="w-full"
            @update:model-value="emits('update:new-dataset-name', $event)"
            @keydown.enter.prevent="emits('create')"
          />
        </UFormField>
        <div class="flex justify-end gap-2">
          <UButton
            label="Discard"
            color="neutral"
            variant="ghost"
            size="sm"
            :disabled="adding"
            @click="emits('cancel-create')"
          />
          <UButton
            label="Create dataset"
            color="primary"
            variant="outline"
            size="sm"
            :disabled="!newDatasetName.trim() || adding"
            :loading="creatingDataset"
            @click="emits('create')"
          />
        </div>
      </div>
      <UButton
        v-else
        label="New dataset"
        icon="i-hugeicons-plus-sign"
        color="neutral"
        variant="ghost"
        size="sm"
        class="-ms-2"
        :disabled="adding"
        @click="emits('start-create')"
      />

      <UAlert
        v-if="error"
        :title="errorTitle"
        :description="error"
        icon="i-hugeicons-alert-02"
        color="error"
        variant="subtle"
      >
        <template v-if="errorCode === 'version_conflict'" #actions>
          <UButton
            label="Reload version"
            color="neutral"
            variant="outline"
            size="xs"
            :loading="loadingSelectedDataset"
            @click="emits('reload-version')"
          />
        </template>
      </UAlert>
    </main>

    <footer
      class="sticky bottom-0 flex shrink-0 justify-end gap-2 border-t border-default bg-default px-4 py-3"
    >
      <UButton
        label="Cancel"
        color="neutral"
        variant="outline"
        size="sm"
        :disabled="adding"
        @click="emits('cancel')"
      />
      <UButton
        :label="alreadyPresent ? 'Already in dataset' : addLabel"
        :color="alreadyPresent || !canAdd ? 'neutral' : 'primary'"
        :variant="alreadyPresent || !canAdd ? 'outline' : 'solid'"
        size="sm"
        :disabled="!canAdd || adding"
        :loading="adding"
        @click="emits('submit')"
      />
    </footer>
  </section>
</template>

<script setup lang="ts">
import type {
  CodeReviewEvaluationDatasetSummaryDto,
  CodeReviewPullRequestDto,
} from "@/api/generated";
import { getInboxDatasetRowState } from "./inbox-add-dataset-view-models";

const props = defineProps<{
  pullRequest: CodeReviewPullRequestDto;
  embedded: boolean;
  datasets: CodeReviewEvaluationDatasetSummaryDto[];
  selectedDatasetId: string | null;
  search: string;
  loadingDatasets: boolean;
  loadingSelectedDataset: boolean;
  creatingNew: boolean;
  creatingDataset: boolean;
  adding: boolean;
  newDatasetName: string;
  error: string | null;
  errorCode: string | null;
  canAdd: boolean;
  canOpenEditor: boolean;
  alreadyPresent: boolean;
  reviewerIds: string[];
  addLabel: string;
  maxCases: number;
  selectedDataset: {
    id: string;
    cases: {
      pullRequestNumber: number | string;
      removedAtUtc?: Date | string | null;
    }[];
  } | null;
  versionFor: (
    dataset: CodeReviewEvaluationDatasetSummaryDto,
  ) => number | string;
}>();

const emits = defineEmits<{
  back: [];
  cancel: [];
  submit: [];
  select: [datasetId: string];
  "update:search": [value: string];
  "update:new-dataset-name": [value: string];
  "start-create": [];
  "cancel-create": [];
  create: [];
  "reload-version": [];
  "open-editor": [datasetId: string, reviewerIds: string[]];
}>();

const description = computed(
  () =>
    `PR #${props.pullRequest.pullRequestNumber} · ${props.pullRequest.title}`,
);
const errorTitle = computed(() => "Add status");

function datasetDisabled(dataset: CodeReviewEvaluationDatasetSummaryDto) {
  return !getInboxDatasetRowState({
    caseCount: Number(dataset.caseCount),
    maxCases: props.maxCases,
    containsPullRequest: containsSelectedPullRequest(dataset),
    mutating: props.adding || props.creatingDataset,
  }).selectable;
}

function containsSelectedPullRequest(
  dataset: CodeReviewEvaluationDatasetSummaryDto,
) {
  return (
    props.selectedDataset?.id === dataset.id &&
    props.selectedDataset.cases.some(
      (item) =>
        Number(item.pullRequestNumber) ===
          Number(props.pullRequest.pullRequestNumber) && !item.removedAtUtc,
    )
  );
}
</script>
