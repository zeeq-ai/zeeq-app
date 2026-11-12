<template>
  <section class="grid min-w-0 gap-2">
    <h3 class="text-sm font-semibold text-highlighted">
      Per-PR results ({{ cases.length }})
    </h3>
    <UEmpty
      v-if="cases.length === 0 && !loading"
      icon="i-hugeicons-file-search"
      title="No case results"
      description="This run has no case results to display yet."
      class="py-6"
    />
    <div
      v-else
      class="min-w-0 overflow-x-auto rounded-md border border-default"
    >
      <UTable
        v-model:expanded="expandedRows"
        :data="cases"
        :columns="columns"
        :loading="loading"
        :get-row-id="(item: EvaluationRunCaseDto) => item.caseId"
        :ui="{
          base: 'w-full table-fixed sm:table-auto',
          th: 'px-2 py-1.5 text-[11px]',
          td: 'px-2 py-1.5 text-xs',
          tr: 'h-8 data-[expanded=true]:bg-elevated/30',
        }"
        class="min-w-0 sm:min-w-[58rem]"
      >
        <template #expand-cell="{ row }">
          <UButton
            :icon="
              row.getIsExpanded()
                ? 'i-hugeicons-arrow-down-01'
                : 'i-hugeicons-arrow-right-01'
            "
            color="neutral"
            variant="ghost"
            size="xs"
            square
            :aria-label="
              row.getIsExpanded()
                ? 'Collapse case details'
                : 'Expand case details'
            "
            :aria-expanded="row.getIsExpanded()"
            @click="row.toggleExpanded()"
          />
        </template>
        <template #pr-cell="{ row }"
          ><span class="font-mono tabular-nums text-muted"
            >#{{ row.original.pullRequestNumber }}</span
          ></template
        >
        <template #title-cell="{ row }"
          ><UTooltip :text="row.original.title"
            ><span class="block max-w-28 truncate sm:max-w-56">{{
              row.original.title
            }}</span></UTooltip
          ></template
        >
        <template #reference-cell="{ row }"
          ><span class="tabular-nums">{{
            referenceDisplay(row.original)
          }}</span></template
        >
        <template #covered-cell="{ row }"
          ><span class="tabular-nums">{{
            gradeCounts(row.original).covered
          }}</span></template
        >
        <template #missed-cell="{ row }"
          ><span
            class="tabular-nums"
            :class="
              hasMissedReferences(row.original) ? 'text-warning' : 'text-muted'
            "
            >{{ gradeCounts(row.original).missed }}</span
          ></template
        >
        <template #additional-cell="{ row }"
          ><span class="tabular-nums">{{
            gradeCounts(row.original).additional
          }}</span></template
        >
        <template #status-cell="{ row }"
          ><span
            class="block max-w-24 truncate text-muted sm:max-w-none sm:whitespace-nowrap"
            >{{ statusLabel(row.original) }}</span
          ></template
        >
        <template #expanded="{ row }">
          <div class="grid min-w-0 gap-3 p-3">
            <div
              class="flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted sm:hidden"
            >
              <span>Reference {{ referenceDisplay(row.original) }}</span
              ><span>Covered {{ gradeCounts(row.original).covered }}</span
              ><span>Missed {{ gradeCounts(row.original).missed }}</span
              ><span
                >Additional {{ gradeCounts(row.original).additional }}</span
              >
            </div>
            <template v-if="row.original.outcome !== 'Graded'">
              <p class="text-sm text-muted">
                Ungraded findings (unverified) ·
                {{ statusLabel(row.original) }}; no assessment was recorded.
              </p>
              <div
                v-for="finding in row.original.candidateFindings"
                :key="finding.id"
                class="flex min-w-0 flex-col items-start justify-between gap-3 rounded border sm:flex-row border-default p-2"
              >
                <div class="min-w-0">
                  <p class="text-sm font-medium text-highlighted">
                    {{ finding.summary }}
                  </p>
                  <p class="text-xs text-muted">{{ finding.details }}</p>
                  <p class="font-mono text-xs text-muted">
                    {{ finding.file
                    }}<span v-if="finding.line">:{{ finding.line }}</span> ·
                    {{ finding.severity }}
                  </p>
                </div>
                <div class="flex shrink-0 items-center gap-2">
                  <UBadge
                    label="Unverified"
                    color="neutral"
                    variant="subtle"
                    size="xs"
                  /><UButton
                    v-if="row.original.references.state !== 'Unlabelled'"
                    label="Add to reference…"
                    size="xs"
                    color="neutral"
                    variant="outline"
                    @click="emits('addToReference', row.original, finding.id)"
                  />
                </div>
              </div>
              <p
                v-if="row.original.candidateFindings.length === 0"
                class="text-xs text-muted"
              >
                The reviewer returned no findings.
              </p>
            </template>
            <template v-else>
              <div
                v-if="row.original.references.state === 'Unlabelled'"
                class="text-sm text-muted"
              >
                Unlabelled · excluded from coverage.
              </div>
              <div
                v-else-if="row.original.references.state === 'ReviewedClean'"
                class="text-sm text-muted"
              >
                Reviewed clean · expected no findings.
              </div>
              <div v-else class="grid gap-1">
                <div
                  class="grid grid-cols-1 sm:grid-cols-[minmax(10rem,1fr)_minmax(12rem,1fr)_minmax(10rem,1fr)] gap-3 border-b border-default px-2 py-1 text-[11px] font-semibold uppercase text-muted"
                >
                  <span>Reference</span><span>Run finding</span
                  ><span>Assessment</span>
                </div>
                <div
                  v-for="reference in row.original.references.findings"
                  :key="reference.id"
                  class="grid grid-cols-1 sm:grid-cols-[minmax(10rem,1fr)_minmax(12rem,1fr)_minmax(10rem,1fr)] gap-3 border-b border-default px-2 py-2 text-xs"
                >
                  <div>
                    <p class="font-medium text-highlighted">
                      {{ reference.title }}
                    </p>
                    <p class="font-mono text-muted">
                      {{ reference.file
                      }}<span v-if="reference.line">:{{ reference.line }}</span>
                    </p>
                  </div>
                  <div v-if="candidateForReference(row.original, reference.id)">
                    <p class="text-default">
                      {{
                        candidateForReference(row.original, reference.id)
                          ?.summary
                      }}
                    </p>
                    <p class="font-mono text-muted">
                      {{
                        candidateForReference(row.original, reference.id)?.file
                      }}<span
                        v-if="
                          candidateForReference(row.original, reference.id)
                            ?.line
                        "
                        >:{{
                          candidateForReference(row.original, reference.id)
                            ?.line
                        }}</span
                      >
                    </p>
                  </div>
                  <p v-else class="text-warning">Missed</p>
                  <p class="text-muted">
                    {{ assessment(row.original, reference.id) }}
                  </p>
                </div>
              </div>
              <section
                v-if="additionalFindings(row.original).length"
                class="grid gap-2 border-t border-default pt-3"
              >
                <h4 class="text-xs font-semibold text-highlighted">
                  Additional · unverified ({{
                    additionalFindings(row.original).length
                  }})
                </h4>
                <article
                  v-for="finding in additionalFindings(row.original)"
                  :key="finding.id"
                  class="flex min-w-0 flex-col items-start justify-between gap-3 rounded border sm:flex-row border-default p-2"
                >
                  <div class="min-w-0">
                    <p class="text-sm font-medium text-highlighted">
                      {{ finding.summary }}
                    </p>
                    <p class="text-xs text-muted">{{ finding.details }}</p>
                    <p class="font-mono text-xs text-muted">
                      {{ finding.file
                      }}<span v-if="finding.line">:{{ finding.line }}</span> ·
                      {{ finding.severity }}
                    </p>
                  </div>
                  <UButton
                    v-if="row.original.references.state !== 'Unlabelled'"
                    label="Add to reference…"
                    size="xs"
                    color="neutral"
                    variant="outline"
                    class="shrink-0"
                    @click="emits('addToReference', row.original, finding.id)"
                  />
                </article>
              </section>
            </template>
          </div>
        </template>
      </UTable>
    </div>
  </section>
</template>

<script setup lang="ts">
import { ref } from "vue";
import type { TableColumn } from "@nuxt/ui";
import type { EvaluationRunCaseDto } from "@/api/generated";
import { getEvaluationCaseGradeCounts } from "./evaluation-view-models";

const props = defineProps<{
  cases: EvaluationRunCaseDto[];
  loading: boolean;
}>();
const emits = defineEmits<{
  addToReference: [result: EvaluationRunCaseDto, findingId: string];
}>();
const expandedRows = ref<Record<string, boolean>>({});
const columns: TableColumn<EvaluationRunCaseDto>[] = [
  { id: "expand", header: "", meta: { class: { th: "w-8", td: "w-8" } } },
  { id: "pr", header: "PR", meta: { class: { th: "w-14" } } },
  {
    id: "title",
    header: "Title",
    meta: { class: { th: "min-w-0 sm:min-w-36" } },
  },
  {
    id: "reference",
    header: "Reference",
    meta: {
      class: {
        th: "hidden text-right sm:table-cell",
        td: "hidden text-right tabular-nums sm:table-cell",
      },
    },
  },
  {
    id: "covered",
    header: "Covered",
    meta: {
      class: {
        th: "hidden text-right sm:table-cell",
        td: "hidden text-right tabular-nums sm:table-cell",
      },
    },
  },
  {
    id: "missed",
    header: "Missed",
    meta: {
      class: {
        th: "hidden text-right sm:table-cell",
        td: "hidden text-right tabular-nums sm:table-cell",
      },
    },
  },
  {
    id: "additional",
    header: "Additional",
    meta: {
      class: {
        th: "hidden text-right sm:table-cell",
        td: "hidden text-right tabular-nums sm:table-cell",
      },
    },
  },
  {
    id: "status",
    header: "Status",
    meta: { class: { th: "w-24 sm:w-auto", td: "break-words" } },
  },
];
function gradeCounts(result: EvaluationRunCaseDto) {
  return getEvaluationCaseGradeCounts(result);
}
function hasMissedReferences(result: EvaluationRunCaseDto): boolean {
  const missed = getEvaluationCaseGradeCounts(result).missed;
  return typeof missed === "number" && missed > 0;
}
function referenceDisplay(result: EvaluationRunCaseDto): number | string {
  return getEvaluationCaseGradeCounts(result).reference;
}
function additionalFindings(result: EvaluationRunCaseDto) {
  const matched = new Set(
    result.judgeMatches.map((match) => match.candidateId),
  );
  return result.candidateFindings.filter(
    (candidate) => !matched.has(candidate.id),
  );
}
function candidateForReference(
  result: EvaluationRunCaseDto,
  referenceId: string,
) {
  const id = result.judgeMatches.find(
    (match) => match.referenceId === referenceId,
  )?.candidateId;
  return result.candidateFindings.find((candidate) => candidate.id === id);
}
function assessment(result: EvaluationRunCaseDto, referenceId: string): string {
  return (
    result.judgeMatches.find((match) => match.referenceId === referenceId)
      ?.reason ?? "No matching reviewer finding."
  );
}
function statusLabel(result: EvaluationRunCaseDto): string {
  // Reference labels are terminal assessment states, not execution progress.
  if (result.outcome === "None") {
    if (result.phase === "Pending") return "Queued";
    if (result.phase === "Reviewing") return "Running";
    if (result.phase === "Reviewed") return "Awaiting grade";
    return result.phase;
  }
  if (result.outcome === "Cancelled") return "Cancelled";
  if (result.outcome === "Skipped")
    return `Skipped${result.errorCode ? ` · ${result.errorCode}` : ""}`;
  if (result.outcome === "ReviewFailed") return "Review failed";
  if (result.outcome === "JudgeFailed") return "Judge failed";
  if (
    result.outcome === "Unlabelled" ||
    result.references.state === "Unlabelled"
  )
    return "Unlabelled";
  if (result.references.state === "ReviewedClean") return "Reviewed clean";
  return result.outcome === "Graded" ? "Graded" : result.phase;
}
</script>
