<template>
  <div class="mx-auto grid min-w-0 max-w-4xl grid-cols-[minmax(0,1fr)] gap-4">
    <header class="flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0">
        <h3 class="font-semibold text-highlighted">
          #{{ evaluationCase.pullRequestNumber }}
          {{ evaluationCase.snapshot.title }}
        </h3>
        <p class="mt-1 break-all font-mono text-xs text-muted">
          {{ evaluationCase.snapshot.baseSha.slice(0, 8) }} →
          {{ evaluationCase.snapshot.headSha.slice(0, 8) }} · Captured
          {{ formatCaptureTime(evaluationCase.snapshot.capturedAtUtc) }}
        </p>
        <p class="mt-1 text-xs text-muted">
          {{ evaluationCase.snapshot.changedFileCount }} changed
          {{ evaluationCase.snapshot.changedFileCount === 1 ? "file" : "files"
          }}<span
            v-if="evaluationCase.snapshot.omittedFileCount > 0"
            class="text-warning"
          >
            · {{ evaluationCase.snapshot.omittedFileCount }} patch omitted</span
          >
        </p>
      </div>
      <div class="flex flex-wrap gap-2">
        <UButton
          label="Refresh snapshot"
          icon="i-hugeicons-refresh"
          color="neutral"
          variant="outline"
          size="sm"
          :loading="refreshing"
          :disabled="saving || !evaluationCase.caseId"
          @click="emits('refresh')"
        />
        <UButton
          label="Remove"
          icon="i-hugeicons-delete-02"
          color="neutral"
          variant="ghost"
          size="sm"
          :disabled="saving"
          @click="emits('remove')"
        />
      </div>
    </header>

    <UAlert
      v-if="saveError"
      title="Dataset changes were not saved"
      :description="saveError"
      color="error"
      variant="subtle"
    />
    <UAlert
      v-if="evaluationCase.snapshot.omittedFileCount > 0"
      title="This snapshot omits some file patches"
      :description="`${evaluationCase.snapshot.omittedFileCount} changed files have no patch. The evaluation will use only the captured patches.`"
      color="warning"
      variant="subtle"
    />

    <UTabs
      v-model="detailPane"
      :items="detailPaneItems"
      color="neutral"
      variant="link"
    >
      <template #diff>
        <div class="grid gap-3 pt-4">
          <UAlert
            v-if="!evaluationCase.caseId || evaluationCase.captureToken"
            title="Save to inspect this captured diff"
            description="This capture is staged. Save the dataset first, then inspect the newly stored patches."
            color="info"
            variant="subtle"
          />
          <div v-else-if="snapshotLoading" class="grid gap-2">
            <USkeleton class="h-10" /><USkeleton class="h-32" />
          </div>
          <UAlert
            v-else-if="!snapshot"
            title="Captured diff unavailable"
            description="The stored snapshot could not be loaded. Retry after confirming access to this dataset."
            color="error"
            variant="subtle"
          />
          <UAccordion
            v-else
            :items="fileItems"
            type="multiple"
            :default-value="[]"
            :ui="{
              root: 'rounded-md border border-default',
              item: 'border-b border-default last:border-b-0',
              trigger: 'px-3 py-2',
              body: 'px-3 pb-3',
            }"
          >
            <template #default="{ item }">
              <div class="flex min-w-0 items-center gap-2">
                <span class="truncate font-mono text-xs text-highlighted">{{
                  item.file.path
                }}</span>
                <UBadge
                  :label="item.file.mutationState"
                  color="neutral"
                  variant="subtle"
                  size="xs"
                />
                <UBadge
                  v-if="item.file.isOmitted"
                  label="Patch omitted"
                  color="warning"
                  variant="subtle"
                  size="xs"
                />
              </div>
            </template>
            <template #body="{ item }">
              <p v-if="item.file.previousPath" class="mb-2 text-xs text-muted">
                Renamed from {{ item.file.previousPath }}
              </p>
              <p v-if="item.file.isOmitted" class="text-sm text-muted">
                No patch is available for this file.
              </p>
              <pre
                v-else
                class="snapshot-diff rounded bg-elevated p-3 font-mono text-xs whitespace-pre"
                >{{ item.file.patch }}</pre>
            </template>
          </UAccordion>
          <KeyValue v-if="snapshot" :pairs="snapshotFacts" />
        </div>
      </template>
      <template #reference-findings>
        <div class="grid gap-3 pt-4">
          <UButton
            label="Import historical findings"
            icon="i-hugeicons-clock-01"
            color="neutral"
            variant="outline"
            size="sm"
            class="w-fit"
            :disabled="saving || !evaluationCase.caseId"
            :loading="loadingHistorical"
            @click="emits('importHistorical')"
          />
          <p
            v-if="
              historicalLookupComplete &&
              historicalFindings.length === 0 &&
              !loadingHistorical
            "
            class="text-xs text-muted"
          >
            No previous findings were found for this reviewer on this PR.
          </p>
          <ReferenceFindingEditor
            :reference-set="referenceSet"
            :disabled="saving"
            @update="emits('updateReferenceSet', $event)"
          />
          <ul v-if="historicalFindings.length" class="grid gap-2">
            <li
              v-for="(finding, index) in historicalFindings"
              :key="`${finding.sourceFindingId}:${index}`"
              class="flex items-start justify-between gap-3 rounded border border-default p-2"
            >
              <div class="min-w-0">
                <p class="text-sm font-medium text-highlighted">
                  {{ finding.title }}
                </p>
                <p class="text-xs text-muted">
                  {{ finding.file
                  }}<span v-if="finding.line">:{{ finding.line }}</span> ·
                  {{ finding.severity }}
                </p>
              </div>
              <UButton
                label="Add"
                size="xs"
                color="neutral"
                variant="ghost"
                :disabled="saving"
                @click="emits('addHistorical', finding)"
              />
            </li>
          </ul>
        </div>
      </template>
    </UTabs>
  </div>
</template>

<script setup lang="ts">
import type {
  CodeReviewEvaluationReferenceFindingDto,
  CodeReviewEvaluationReferenceSetDto,
  CodeReviewEvaluationSnapshot,
} from "@/api/generated";
import type { EvaluationCaseDraft } from "./evaluation-view-models";
import ReferenceFindingEditor from "./ReferenceFindingEditor.vue";

const props = defineProps<{
  evaluationCase: EvaluationCaseDraft;
  referenceSet: CodeReviewEvaluationReferenceSetDto;
  snapshot: CodeReviewEvaluationSnapshot | null;
  snapshotLoading: boolean;
  historicalFindings: CodeReviewEvaluationReferenceFindingDto[];
  historicalLookupComplete: boolean;
  loadingHistorical: boolean;
  saving: boolean;
  refreshing: boolean;
  saveError: string | null;
}>();
const emits = defineEmits<{
  refresh: [];
  remove: [];
  updateReferenceSet: [referenceSet: CodeReviewEvaluationReferenceSetDto];
  importHistorical: [];
  addHistorical: [finding: CodeReviewEvaluationReferenceFindingDto];
}>();
const detailPane = ref("reference-findings");
const detailPaneItems = [
  { label: "Diff", value: "diff", slot: "diff" },
  {
    label: "Reference findings",
    value: "reference-findings",
    slot: "reference-findings",
  },
];
const fileItems = computed(() =>
  (props.snapshot?.files ?? []).map((file, index) => ({
    label: file.path,
    value: `${index}:${file.path}`,
    file,
  })),
);
const snapshotFacts = computed(() =>
  props.snapshot
    ? {
        "Snapshot SHA-256":
          props.evaluationCase.snapshot.snapshotSha256 ??
          "Verified stored artifact",
        "Base commit": props.snapshot.baseSha,
        "Head commit": props.snapshot.headSha,
        "Changed files":
          props.snapshot.changedFileCount ??
          props.snapshot.declaredChangedFileCount,
        "Omitted patches":
          props.snapshot.omittedFileCount ??
          props.evaluationCase.snapshot.omittedFileCount,
      }
    : {},
);

function formatCaptureTime(value: Date | string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value instanceof Date ? value : new Date(value));
}
</script>

<style scoped>
.snapshot-diff {
  max-height: 26rem;
  overflow: auto;
}
</style>
