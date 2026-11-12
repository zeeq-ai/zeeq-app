<template>
  <div
    class="flex flex-wrap items-end gap-x-4 gap-y-2 border-b border-default p-3"
  >
    <UFormField label="Dataset" class="w-full min-w-0 lg:w-auto lg:flex-[1.15]">
      <UFieldGroup size="md" class="w-full">
        <USelectMenu
          :model-value="selectedDatasetId ?? undefined"
          :items="datasetItems"
          value-key="value"
          aria-label="Evaluation dataset"
          :title="
            datasetItems.find((item) => item.value === selectedDatasetId)?.label
          "
          placeholder="Choose a dataset"
          color="neutral"
          variant="outline"
          size="md"
          class="min-w-0 flex-1 lg:min-w-56"
          :disabled="disabled || datasets.length === 0"
          @update:model-value="emits('selectDataset', $event)"
        />
        <UTooltip text="Manage datasets">
          <UButton
            label="Manage datasets"
            aria-label="Manage datasets"
            icon="i-hugeicons-layers-01"
            color="neutral"
            variant="outline"
            size="md"
            class="shrink-0"
            :disabled="disabled"
            @click="emits('manageDatasets')"
          />
        </UTooltip>
      </UFieldGroup>
    </UFormField>
    <USeparator orientation="vertical" class="hidden h-8 self-end lg:block" />
    <UFormField label="Run with" class="w-full min-w-0 lg:w-auto lg:flex-1">
      <UFieldGroup size="md" class="w-full">
        <USelectMenu
          :model-value="revisionValue"
          :items="revisionItems"
          value-key="value"
          aria-label="Reviewer revision to evaluate"
          color="neutral"
          variant="outline"
          size="md"
          class="min-w-0 flex-1 lg:min-w-40"
          :disabled="disabled || revisions.length === 0"
          @update:model-value="
            emits('selectRevision', $event === draftRevisionValue ? '' : $event)
          "
        />
        <UTooltip v-if="selectedRevisionId" text="View revision diff">
          <UButton
            icon="i-hugeicons-git-compare"
            aria-label="View revision"
            color="neutral"
            variant="outline"
            size="md"
            :disabled="disabled"
            @click="emits('viewRevision')"
          />
        </UTooltip>
        <UButton
          aria-label="Run evaluation"
          icon="i-hugeicons-play"
          color="primary"
          size="md"
          class="shrink-0"
          :loading="running"
          :disabled="
            disabled ||
            !selectedDatasetId ||
            datasets.length === 0 ||
            !revisionValue
          "
          @click="runEvaluation"
        >
          <span class="hidden sm:inline">Run evaluation</span
          ><span class="sm:hidden">Run</span>
        </UButton>
      </UFieldGroup>
    </UFormField>
  </div>
</template>

<script setup lang="ts">
import type {
  CodeReviewEvaluationDatasetSummaryDto,
  CodeReviewerAgentRevisionSummaryDto,
} from "@/api/generated";

const props = defineProps<{
  datasets: CodeReviewEvaluationDatasetSummaryDto[];
  selectedDatasetId: string | null;
  revisions: CodeReviewerAgentRevisionSummaryDto[];
  selectedRevisionId: string | null;
  disabled: boolean;
  running: boolean;
}>();

const emits = defineEmits<{
  selectDataset: [datasetId: string];
  selectRevision: [revisionId: string];
  manageDatasets: [];
  viewRevision: [];
  run: [revisionId: string | null];
}>();

const draftRevisionValue = "__current_draft__";

const datasetItems = computed(() =>
  props.datasets.map((dataset) => ({
    label: `${dataset.name} · v${dataset.version} · ${dataset.caseCount} PRs`,
    value: dataset.id,
  })),
);

const revisionItems = computed(() => [
  { label: "Current draft", value: draftRevisionValue },
  ...props.revisions.map((revision) => ({
    label: revision.label || `Revision ${revision.id.slice(0, 8)}`,
    value: revision.id,
  })),
]);

const revisionValue = computed(
  () =>
    revisionItems.value.find((item) => item.value === props.selectedRevisionId)
      ?.value ?? draftRevisionValue,
);

function runEvaluation() {
  emits(
    "run",
    revisionValue.value === draftRevisionValue ? null : revisionValue.value,
  );
}
</script>
