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
      description="Unlabelled cases are excluded from reference coverage until you mark them expected or reviewed clean."
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
      v-if="referenceState !== 'Expected' && findings.length > 0"
      class="text-sm text-muted"
      role="status"
    >
      Saving this state removes the expected findings. Switch back to Expected
      before saving to keep them.
    </p>

    <div
      v-if="referenceState === 'Expected'"
      class="grid min-w-0 grid-cols-[minmax(0,1fr)] gap-3"
    >
      <div class="flex flex-wrap items-center justify-between gap-2">
        <p class="text-sm text-muted">
          {{ findings.length }} expected finding{{
            findings.length === 1 ? "" : "s"
          }}
        </p>
        <UButton
          label="Add reference finding"
          icon="i-hugeicons-plus-sign"
          color="neutral"
          variant="outline"
          size="sm"
          :disabled="disabled || findings.length >= 100"
          @click="addFinding"
        />
      </div>

      <UEmpty
        v-if="findings.length === 0"
        icon="i-hugeicons-search-01"
        title="Add an expected finding"
        description="A case with no expected findings should use Reviewed clean."
        class="py-8"
      />

      <div v-for="item in findingItems" :key="item.value" class="relative">
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
                        $event === null ? null : Number($event),
                      )
                    "
                /></UFormField>
              </div>
            </div>
          </template>
        </UAccordion>
        <UDropdownMenu
          :items="findingMenuItems(item.finding.id)"
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
  </div>
</template>

<script setup lang="ts">
import type {
  CodeReviewEvaluationReferenceFindingDto,
  CodeReviewEvaluationReferenceSetDto,
} from "@/api/generated";
import type { DropdownMenuItem } from "@nuxt/ui";

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
const findingItems = computed(() =>
  findings.value.map((finding) => ({
    label: finding.title || "Untitled expected finding",
    value: finding.id,
    finding,
  })),
);

function addFinding() {
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
  };
  expandedFindingIds.value = [...expandedFindingIds.value, finding.id];
  updateReferenceSet({
    ...props.referenceSet,
    state: "Expected",
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

function findingMenuItems(findingId: string): DropdownMenuItem[][] {
  return [
    [
      {
        label: "Delete finding",
        icon: "i-hugeicons-delete-02",
        color: "error",
        onSelect: () => removeFinding(findingId),
      },
    ],
  ];
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
  field: "title" | "summary" | "file" | "line" | "severity",
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
