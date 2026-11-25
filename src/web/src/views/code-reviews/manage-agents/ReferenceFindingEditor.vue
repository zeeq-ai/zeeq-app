<template>
  <div class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-4">
    <UFormField label="Reference state">
      <URadioGroup
        v-model="referenceState"
        :items="referenceStateItems"
        orientation="horizontal"
        :ui="{ root: 'min-w-0', fieldset: 'min-w-0 flex-wrap' }"
        :disabled="disabled"
        aria-label="Expected finding state for this reviewer"
      />
    </UFormField>

    <UAlert
      v-if="referenceState === 'Unlabelled'"
      title="No grade for this PR"
      :description="unlabelledDescription"
      color="neutral"
      variant="subtle"
    />
    <UAlert
      v-else-if="referenceState === 'ReviewedClean'"
      title="Reviewed clean"
      description="A clean reference expects no findings from this reviewer."
      color="success"
      variant="subtle"
    />
    <p
      v-if="referenceState !== 'Expected' && expectedFindings.length > 0"
      class="text-sm text-muted"
      role="status"
    >
      Saving this state removes expected findings. Known false positives remain
      active. Switch back to Expected before saving to keep expected findings.
    </p>

    <div
      v-if="referenceState === 'Expected'"
      class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-3"
    >
      <div class="flex flex-wrap items-center justify-between gap-2">
        <p class="text-sm text-muted">
          {{ expectedFindings.length }} expected finding{{
            expectedFindings.length === 1 ? "" : "s"
          }}
        </p>
        <UButton
          label="Add reference finding"
          icon="i-hugeicons-plus-sign"
          color="neutral"
          variant="outline"
          size="sm"
          :disabled="disabled || findings.length >= 100"
          @click="addFinding('Expected')"
        />
      </div>

      <UEmpty
        v-if="expectedFindings.length === 0"
        icon="i-hugeicons-search-01"
        title="Add an expected finding"
        description="A case with no expected findings should use Reviewed clean."
        class="py-8"
      />

      <div v-for="item in expectedItems" :key="item.value" class="relative">
        <UAccordion
          type="multiple"
          :items="[item]"
          :model-value="
            expandedFindingIds.includes(item.value) ? [item.value] : []
          "
          :ui="{
            root: 'rounded-md border border-default',
            item: 'border-0',
            trigger: 'py-2 pl-3 pr-12 hover:bg-elevated/40',
            body: 'px-3 pb-3 pt-1',
          }"
          @update:model-value="
            setExpanded(item.value, Array.isArray($event) ? $event : [])
          "
        >
          <template #default="{ item: row }">
            <div class="flex min-w-0 w-full items-center gap-2">
              <UBadge
                :label="row.finding.severity"
                :color="severityColor(row.finding.severity)"
                variant="subtle"
                size="xs"
              />
              <span
                class="min-w-0 flex-1 truncate text-sm font-medium text-highlighted"
                >{{ row.finding.title || "Untitled expected finding" }}</span
              >
              <span
                class="hidden max-w-52 truncate font-mono text-xs text-muted sm:block"
                >{{ row.finding.file
                }}<span v-if="row.finding.line"
                  >:{{ row.finding.line }}</span
                ></span
              >
            </div>
          </template>
          <template #body="{ item: row }">
            <div class="grid gap-3">
              <UFormField label="Title" required
                ><UInput
                  :model-value="row.finding.title"
                  maxlength="300"
                  class="w-full"
                  :disabled="disabled"
                  @update:model-value="
                    updateFinding(row.finding.id, 'title', $event)
                  "
              /></UFormField>
              <UFormField label="Severity"
                ><USelect
                  :model-value="row.finding.severity"
                  :items="severityItems"
                  class="w-full sm:max-w-56"
                  :disabled="disabled"
                  @update:model-value="
                    updateFinding(row.finding.id, 'severity', $event)
                  "
              /></UFormField>
              <UFormField label="Summary" required
                ><UTextarea
                  :model-value="row.finding.summary"
                  maxlength="4000"
                  :rows="3"
                  autoresize
                  :maxrows="8"
                  class="w-full"
                  :disabled="disabled"
                  @update:model-value="
                    updateFinding(row.finding.id, 'summary', $event)
                  "
              /></UFormField>
              <div class="grid gap-3 sm:grid-cols-[minmax(0,1fr)_10rem]">
                <UFormField label="File"
                  ><UInput
                    :model-value="row.finding.file"
                    class="w-full"
                    :disabled="disabled"
                    @update:model-value="
                      updateFinding(row.finding.id, 'file', $event)
                    "
                /></UFormField>
                <UFormField label="Line" hint="Optional"
                  ><UInputNumber
                    :model-value="
                      row.finding.line === null
                        ? undefined
                        : Number(row.finding.line)
                    "
                    :min="1"
                    :disabled="disabled"
                    class="w-full"
                    @update:model-value="
                      updateFinding(
                        row.finding.id,
                        'line',
                        $event == null ? null : Number($event),
                      )
                    "
                /></UFormField>
              </div>
            </div>
          </template>
        </UAccordion>
        <UDropdownMenu
          :items="findingMenuItems(item.finding.id, 'Expected')"
          :content="{ align: 'end' }"
          class="absolute right-1 top-1"
        >
          <UButton
            icon="i-hugeicons-more-horizontal"
            color="neutral"
            variant="ghost"
            size="xs"
            :disabled="disabled"
            :aria-label="`Actions for ${item.finding.title || 'reference finding'}`"
          />
        </UDropdownMenu>
      </div>
    </div>

    <section class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-3">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <p class="text-sm text-muted">
          {{ rejectedFindings.length }} known false positive{{
            rejectedFindings.length === 1 ? "" : "s"
          }}
        </p>
        <UButton
          label="Add false positive"
          icon="i-hugeicons-plus-sign"
          color="neutral"
          variant="outline"
          size="sm"
          :disabled="disabled || findings.length >= 100"
          @click="addFinding('Rejected')"
        />
      </div>
      <div v-for="item in rejectedItems" :key="item.value" class="relative">
        <UAccordion
          type="multiple"
          :items="[item]"
          :model-value="
            expandedFindingIds.includes(item.value) ? [item.value] : []
          "
          :ui="{
            root: 'rounded-md border border-default',
            item: 'border-0',
            trigger: 'py-2 pl-3 pr-12 hover:bg-elevated/40',
            body: 'px-3 pb-3 pt-1',
          }"
          @update:model-value="
            setExpanded(item.value, Array.isArray($event) ? $event : [])
          "
        >
          <template #default="{ item: row }">
            <div class="flex min-w-0 w-full items-center gap-2">
              <UIcon name="i-hugeicons-flag-01" class="shrink-0 text-muted" />
              <span
                class="min-w-0 flex-1 truncate text-sm font-medium text-highlighted"
                >{{ row.finding.title || "Untitled false positive" }}</span
              >
              <span
                class="hidden max-w-52 truncate font-mono text-xs text-muted sm:block"
                >{{ row.finding.file
                }}<span v-if="row.finding.line"
                  >:{{ row.finding.line }}</span
                ></span
              >
            </div>
          </template>
          <template #body="{ item: row }">
            <div class="grid gap-3">
              <UFormField label="Title" required
                ><UInput
                  :model-value="row.finding.title"
                  maxlength="300"
                  class="w-full"
                  :disabled="disabled"
                  @update:model-value="
                    updateFinding(row.finding.id, 'title', $event)
                  "
              /></UFormField>
              <UFormField label="Summary" required
                ><UTextarea
                  :model-value="row.finding.summary"
                  maxlength="4000"
                  :rows="3"
                  autoresize
                  :maxrows="8"
                  class="w-full"
                  :disabled="disabled"
                  @update:model-value="
                    updateFinding(row.finding.id, 'summary', $event)
                  "
              /></UFormField>
              <UFormField
                label="Why is this wrong? (Optional — helps the judge match accurately)"
                ><UTextarea
                  :model-value="row.finding.rationale ?? ''"
                  maxlength="1000"
                  class="w-full"
                  :disabled="disabled"
                  @update:model-value="
                    updateFinding(row.finding.id, 'rationale', $event)
                  "
              /></UFormField>
              <div class="grid gap-3 sm:grid-cols-[minmax(0,1fr)_10rem]">
                <UFormField label="File"
                  ><UInput
                    :model-value="row.finding.file"
                    class="w-full"
                    :disabled="disabled"
                    @update:model-value="
                      updateFinding(row.finding.id, 'file', $event)
                    "
                /></UFormField>
                <UFormField label="Line" hint="Optional"
                  ><UInputNumber
                    :model-value="
                      row.finding.line === null
                        ? undefined
                        : Number(row.finding.line)
                    "
                    :min="1"
                    :disabled="disabled"
                    class="w-full"
                    @update:model-value="
                      updateFinding(
                        row.finding.id,
                        'line',
                        $event == null ? null : Number($event),
                      )
                    "
                /></UFormField>
              </div>
            </div>
          </template>
        </UAccordion>
        <UAlert
          v-if="
            hasExpectedRejectedFileCollision(referenceSet, item.finding.file)
          "
          description="An expected finding targets this same file/location. Expected wins any tie-break."
          color="warning"
          variant="subtle"
          class="mt-2"
        />
        <UDropdownMenu
          :items="findingMenuItems(item.finding.id, 'Rejected')"
          :content="{ align: 'end' }"
          class="absolute right-1 top-1"
        >
          <UButton
            icon="i-hugeicons-more-horizontal"
            color="neutral"
            variant="ghost"
            size="xs"
            :disabled="disabled"
            :aria-label="`Actions for ${item.finding.title || 'reference finding'}`"
          />
        </UDropdownMenu>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import type {
  CodeReviewEvaluationReferenceFindingDto,
  CodeReviewEvaluationReferenceSetDto,
} from "@/api/generated";
import type { DropdownMenuItem } from "@nuxt/ui";
import { hasExpectedRejectedFileCollision } from "./evaluation-view-models";

const props = defineProps<{
  referenceSet: CodeReviewEvaluationReferenceSetDto;
  disabled?: boolean;
}>();

const emits = defineEmits<{
  update: [referenceSet: CodeReviewEvaluationReferenceSetDto];
}>();

const referenceStateItems = [
  { label: "Unlabelled", value: "Unlabelled" },
  { label: "Expected findings", value: "Expected" },
  { label: "Reviewed clean", value: "ReviewedClean" },
];

const severityItems = [
  { label: "Critical", value: "Critical" },
  { label: "Major", value: "Major" },
  { label: "Minor", value: "Minor" },
  { label: "Suggestion", value: "Suggestion" },
  { label: "Comment", value: "Comment" },
];

const referenceState = computed({
  get: () => props.referenceSet.state,
  set: (state: CodeReviewEvaluationReferenceSetDto["state"]) =>
    updateReferenceSet({
      ...props.referenceSet,
      state,
      findings: findings.value,
    }),
});

const findings = computed(() => props.referenceSet.findings);
const expandedFindingIds = ref<string[]>([]);
const expectedFindings = computed(() =>
  findings.value.filter((finding) => finding.disposition !== "Rejected"),
);
const rejectedFindings = computed(() =>
  findings.value.filter((finding) => finding.disposition === "Rejected"),
);
const unlabelledDescription = computed(() =>
  rejectedFindings.value.length > 0
    ? `Expected finding coverage is ungraded. The next run will check ${rejectedFindings.value.length} known false positive${rejectedFindings.value.length === 1 ? "" : "s"}.`
    : "Unlabelled cases are excluded from reference coverage until you mark them expected or reviewed clean.",
);
const makeItems = (items: CodeReviewEvaluationReferenceFindingDto[]) =>
  items.map((finding) => ({
    label: finding.title,
    value: finding.id,
    finding,
  }));
const expectedItems = computed(() => makeItems(expectedFindings.value));
const rejectedItems = computed(() => makeItems(rejectedFindings.value));

function addFinding(disposition: "Expected" | "Rejected") {
  if (props.disabled || findings.value.length >= 100) {
    return;
  }

  // NOTE: Empty fields are valid while editing. Save validates the completed payload on
  // the server and preserves this draft on failure; a string DTO does not imply nonempty text.
  const finding: CodeReviewEvaluationReferenceFindingDto = {
    id: crypto.randomUUID(),
    title: "",
    summary: "",
    severity: "Major",
    file: "",
    line: null,
    origin: "Manual",
    sourceRunId: null,
    sourceFindingId: null,
    disposition,
    rationale: null,
  };
  expandedFindingIds.value = [...expandedFindingIds.value, finding.id];
  updateReferenceSet({
    ...props.referenceSet,
    state: disposition === "Expected" ? "Expected" : props.referenceSet.state,
    findings: [...findings.value, finding],
  });
}

function removeFinding(findingId: string) {
  expandedFindingIds.value = expandedFindingIds.value.filter(
    (id) => id !== findingId,
  );
  updateReferenceSet({
    ...props.referenceSet,
    findings: findings.value.filter((finding) => finding.id !== findingId),
  });
}

function setExpanded(findingId: string, value: string[]) {
  if (value.includes(findingId))
    expandedFindingIds.value = [
      ...new Set([...expandedFindingIds.value, findingId]),
    ];
  else
    expandedFindingIds.value = expandedFindingIds.value.filter(
      (id) => id !== findingId,
    );
}

function findingMenuItems(
  findingId: string,
  disposition: "Expected" | "Rejected",
): DropdownMenuItem[][] {
  return [
    [
      {
        label:
          disposition === "Expected"
            ? "Move to false positives"
            : "Move to expected findings",
        onSelect: () =>
          moveFinding(
            findingId,
            disposition === "Expected" ? "Rejected" : "Expected",
          ),
      },
      {
        label: "Delete finding",
        icon: "i-hugeicons-delete-02",
        color: "error",
        onSelect: () => removeFinding(findingId),
      },
    ],
  ];
}
function moveFinding(id: string, disposition: "Expected" | "Rejected") {
  if (props.disabled) return;
  const remainingExpected = findings.value.filter(
    (finding) =>
      finding.id !== id && (finding.disposition ?? "Expected") === "Expected",
  ).length;
  updateReferenceSet({
    ...props.referenceSet,
    state:
      disposition === "Expected"
        ? "Expected"
        : props.referenceSet.state === "Expected" && remainingExpected === 0
          ? "Unlabelled"
          : props.referenceSet.state,
    findings: findings.value.map((finding) =>
      finding.id === id
        ? {
            ...finding,
            disposition,
            rationale: finding.rationale ?? null,
            severity: finding.severity ?? "Major",
          }
        : finding,
    ),
  });
}

function severityColor(
  severity: CodeReviewEvaluationReferenceFindingDto["severity"],
): "error" | "warning" | "neutral" | "info" {
  if (severity === "Critical") return "error";
  if (severity === "Major") return "warning";
  if (severity === "Minor") return "info";
  return "neutral";
}

function updateFinding(
  findingId: string,
  field: "title" | "summary" | "file" | "line" | "severity" | "rationale",
  value: string | number | null | undefined,
) {
  const nextValue = field === "line" && value === undefined ? null : value;
  const nextFindings = findings.value.map((finding) =>
    finding.id === findingId ? { ...finding, [field]: nextValue } : finding,
  );
  updateReferenceSet({ ...props.referenceSet, findings: nextFindings });
}

function updateReferenceSet(referenceSet: CodeReviewEvaluationReferenceSetDto) {
  emits("update", referenceSet);
}
</script>
