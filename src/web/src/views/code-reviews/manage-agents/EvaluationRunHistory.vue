<template>
  <section class="grid gap-3">
    <div class="flex items-center justify-between gap-3">
      <h3 class="font-semibold text-highlighted">
        Previous runs ({{ runs.length }})
      </h3>
      <USkeleton v-if="loading && runs.length === 0" class="h-5 w-24" />
    </div>

    <UEmpty
      v-if="runs.length === 0 && !loading"
      icon="i-hugeicons-clock-01"
      title="No evaluation runs yet"
      description="Choose a dataset and reviewer revision, then run an evaluation."
      class="py-10"
    />

    <div v-else class="overflow-x-auto rounded-md border border-default">
      <table class="w-full min-w-[48rem] text-left text-sm">
        <thead class="bg-elevated text-xs text-muted">
          <tr>
            <th scope="col" class="px-3 py-2">Run</th>
            <th scope="col" class="px-3 py-2">Dataset</th>
            <th scope="col" class="px-3 py-2">Run time</th>
            <th scope="col" class="px-3 py-2">Coverage</th>
            <th scope="col" class="px-3 py-2">Cases</th>
            <th scope="col" class="px-3 py-2">Status</th>
            <th scope="col" class="px-3 py-2">
              <span class="sr-only">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="run in runs" :key="run.id" class="border-t border-default">
            <td class="px-3 py-2">
              <button
                type="button"
                class="font-mono text-primary underline-offset-2 hover:underline focus-visible:outline-2 focus-visible:outline-primary"
                @click="emits('select', run.id)"
              >
                E-{{ run.id.slice(-8)
                }}<span v-if="run.parentRunId"> · retry subset</span>
              </button>
            </td>
            <td class="max-w-48 truncate px-3 py-2 text-muted">
              {{ datasetName(run.datasetId) }} · v{{ run.datasetVersion }}
            </td>
            <td class="whitespace-nowrap px-3 py-2 text-muted">
              {{ formatDate(run.createdAtUtc) }}
            </td>
            <td class="whitespace-nowrap px-3 py-2 font-mono text-highlighted">
              {{ coverage(run) }}
            </td>
            <td class="whitespace-nowrap px-3 py-2 text-muted">
              {{ run.counts.gradedCaseCount }}/{{ run.counts.caseCount }} graded
              <span v-if="Number(run.counts.skippedCaseCount) > 0">
                · {{ run.counts.skippedCaseCount }} skipped</span
              >
              <span v-if="Number(run.counts.failedCaseCount) > 0">
                · {{ run.counts.failedCaseCount }} failed</span
              >
            </td>
            <td class="px-3 py-2">
              <UBadge
                :label="run.state"
                :color="stateColor(run.state)"
                variant="subtle"
              />
            </td>
            <td class="px-3 py-2">
              <div class="flex justify-end gap-1">
                <UButton
                  label="View"
                  size="xs"
                  color="neutral"
                  variant="ghost"
                  @click="emits('select', run.id)"
                />
                <UButton
                  v-if="run.state === 'Queued' || run.state === 'Running'"
                  label="Cancel"
                  size="xs"
                  color="neutral"
                  variant="ghost"
                  :loading="
                    pendingActionRunId === run.id && pendingAction === 'cancel'
                  "
                  @click="requestAction(run, 'cancel')"
                />
                <UButton
                  v-if="run.state === 'Queued' || run.state === 'Running'"
                  label="Abandon"
                  size="xs"
                  color="neutral"
                  variant="ghost"
                  :loading="
                    pendingActionRunId === run.id && pendingAction === 'abandon'
                  "
                  @click="requestAction(run, 'abandon')"
                />
                <UButton
                  v-if="canRetryEvaluationRun(run)"
                  label="Retry failed"
                  size="xs"
                  color="neutral"
                  variant="outline"
                  :loading="
                    pendingActionRunId === run.id && pendingAction === 'retry'
                  "
                  @click="requestAction(run, 'retry')"
                />
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="nextCursorId" class="flex justify-center">
      <UButton
        label="Load more runs"
        color="neutral"
        variant="ghost"
        :loading
        @click="emits('loadMore')"
      />
    </div>
  </section>

  <UModal v-model:open="confirmOpen" :title="confirmTitle">
    <template #body>
      <p class="text-sm text-muted">{{ confirmDescription }}</p>
    </template>
    <template #footer>
      <div class="flex w-full justify-end gap-2">
        <UButton
          label="Keep run"
          color="neutral"
          variant="ghost"
          @click="confirmOpen = false"
        />
        <UButton
          :label="confirmButtonLabel"
          color="primary"
          :loading="!!pendingActionRunId"
          @click="confirmAction"
        />
      </div>
    </template>
  </UModal>
</template>

<script setup lang="ts">
import type { EvaluationRunDto } from "@/api/generated";
import { canRetryEvaluationRun } from "./evaluation-view-models";

type RunAction = "cancel" | "abandon" | "retry";

const props = defineProps<{
  runs: EvaluationRunDto[];
  loading: boolean;
  nextCursorId: string | null;
  datasetName: (datasetId: string) => string;
}>();

const emits = defineEmits<{
  select: [runId: string];
  loadMore: [];
  cancel: [runId: string];
  abandon: [runId: string];
  retry: [runId: string];
}>();

const confirmOpen = ref(false);
const pendingRun = ref<EvaluationRunDto | null>(null);
const pendingAction = ref<RunAction | null>(null);
const pendingActionRunId = computed(() => pendingRun.value?.id ?? null);

const confirmTitle = computed(() =>
  pendingAction.value === "retry"
    ? "Retry failed cases?"
    : pendingAction.value === "abandon"
      ? "Abandon evaluation?"
      : "Cancel evaluation?",
);
const confirmDescription = computed(() =>
  pendingAction.value === "retry"
    ? "This creates a new report for failed or interrupted cases only. Successful cases and the parent report remain unchanged."
    : pendingAction.value === "abandon"
      ? "Only queued runs or interrupted runs with an expired worker lease can be abandoned. A live worker remains protected. Finished results are kept, and failed or interrupted cases can be retried afterward."
      : "No new cases will start. Completed case results remain in this run.",
);
const confirmButtonLabel = computed(() =>
  pendingAction.value === "retry"
    ? "Retry failed cases"
    : pendingAction.value === "abandon"
      ? "Abandon run"
      : "Cancel run",
);

function coverage(run: EvaluationRunDto): string {
  const expected = Number(run.counts.expectedReferenceCount);
  if (expected === 0) {
    return "N/A";
  }
  const matched = Number(run.counts.matchedReferenceCount);
  return `${Math.round((matched / expected) * 100)}% (${matched}/${expected})`;
}

function stateColor(
  state: EvaluationRunDto["state"],
): "success" | "warning" | "error" | "neutral" {
  if (state === "Completed") return "success";
  if (state === "Incomplete") return "warning";
  if (
    state === "DispatchFailed" ||
    state === "Cancelled" ||
    state === "Abandoned"
  )
    return "error";
  return "neutral";
}

function formatDate(value: Date | string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value instanceof Date ? value : new Date(value));
}

function requestAction(run: EvaluationRunDto, action: RunAction) {
  pendingRun.value = run;
  pendingAction.value = action;
  confirmOpen.value = true;
}

function confirmAction() {
  const runId = pendingRun.value?.id;
  if (!runId || !pendingAction.value) {
    return;
  }

  if (pendingAction.value === "cancel") emits("cancel", runId);
  else if (pendingAction.value === "abandon") emits("abandon", runId);
  else emits("retry", runId);
  confirmOpen.value = false;
  pendingRun.value = null;
  pendingAction.value = null;
}
</script>
