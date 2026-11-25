<template>
  <UPopover v-model:open="open">
    <UButton
      :label="label"
      :color="triage?.approvalState === 'Proposed' ? 'info' : 'neutral'"
      :variant="
        !triage ||
        (triage.approvalState === 'Confirmed' &&
          triage.disposition === 'FalsePositive')
          ? 'outline'
          : 'subtle'
      "
      size="xs"
      :loading="busy"
    />
    <template #content>
      <div class="grid w-80 gap-3 p-3">
        <template v-if="triage && !editing">
          <p class="text-sm">{{ triage.currentReason }}</p>
          <p class="text-xs text-muted">
            {{ channelLabel }} ·
            {{ new Date(triage.updatedAtUtc).toLocaleString() }} ·
            {{ finding.findingRef }}
          </p>
          <a
            v-if="triage.sourceUrl"
            :href="triage.sourceUrl"
            target="_blank"
            rel="noopener noreferrer"
            >View source</a
          >
          <div class="flex gap-2">
            <UButton
              label="Edit reason"
              size="xs"
              color="neutral"
              variant="outline"
              @click="editing = true"
            />
            <UButton
              label="Clear"
              :disabled="busy"
              size="xs"
              color="neutral"
              variant="outline"
              :loading="busy"
              @click="clear"
            />
          </div>
          <UButton
            v-if="showPromotionAction"
            label="Add to eval dataset as rejected"
            color="neutral"
            variant="outline"
            size="xs"
            :disabled="busy"
            @click="promote"
          />
        </template>
        <template v-if="editing">
          <fieldset class="grid gap-2">
            <legend class="text-sm font-medium">Decision</legend>
            <URadioGroup v-model="disposition" :items="options" />
          </fieldset>
          <div class="grid gap-1">
            <label :for="reasonInputId" class="text-sm font-medium"
              >Reason</label
            >
            <UTextarea
              :id="reasonInputId"
              v-model="reason"
              :maxlength="1000"
              placeholder="Explain your decision"
              :aria-describedby="reasonHintId"
            />
            <p :id="reasonHintId" class="text-xs text-muted">
              A reason is required. {{ reason.length }}/1000 characters
            </p>
          </div>
          <div class="flex gap-2">
            <UButton
              :label="triage ? 'Save changes' : 'Save'"
              size="xs"
              :disabled="!reason.trim() || busy"
              :loading="busy"
              @click="save"
            />
            <UButton
              label="Cancel"
              color="neutral"
              variant="ghost"
              size="xs"
              :disabled="busy"
              @click="cancel"
            />
          </div>
        </template>
      </div>
    </template>
  </UPopover>
  <UButton
    v-if="triage?.approvalState === 'Proposed'"
    label="Confirm Proposal"
    data-proposal-confirm
    :aria-describedby="proposalContextId"
    color="primary"
    variant="soft"
    size="xs"
    :loading="busy"
    :class="highlight ? 'ring-2 ring-primary' : ''"
    @click="emits('confirm')"
  />
</template>
<script setup lang="ts">
import type {
  CodeReviewFindingDto,
  CodeReviewFindingDisposition,
} from "@/api/generated";
const props = defineProps<{
  finding: CodeReviewFindingDto;
  busy: boolean;
  highlight?: boolean;
  successVersion?: string;
  canPromoteConfirmedFalsePositive?: boolean;
}>();
const emits = defineEmits<{
  save: [
    disposition: CodeReviewFindingDisposition,
    reason: string,
    requestId: string,
  ];
  clear: [requestId: string];
  confirm: [];
  promote: [];
}>();
const open = ref(false);
const editing = ref(false);
const disposition =
  ref<NonNullable<CodeReviewFindingDisposition>>("FalsePositive");
const reason = ref("");
const options = [
  { label: "Mark False Positive", value: "FalsePositive" },
  { label: "Defer / won't fix (this PR)", value: "Deferred" },
];
const triage = computed(() => props.finding.triage);
const showPromotionAction = computed(
  () =>
    props.canPromoteConfirmedFalsePositive === true &&
    triage.value?.approvalState === "Confirmed" &&
    triage.value.disposition === "FalsePositive",
);
const channelLabel = computed(() =>
  triage.value?.channel === "Mcp"
    ? "Agent proposal"
    : triage.value?.channel === "GitHub"
      ? "GitHub"
      : "Web",
);
const label = computed(() =>
  !triage.value
    ? "Triage"
    : triage.value.approvalState === "Proposed"
      ? "Proposed — awaiting human confirmation"
      : triage.value.disposition === "Deferred"
        ? "Defer / won't fix (this PR)"
        : "Confirmed False Positive",
);
const reasonInputId = computed(
  () => `triage-reason-${props.finding.findingRef}`,
);
const reasonHintId = computed(() => `${reasonInputId.value}-hint`);
const proposalContextId = computed(
  () => `triage-context-${props.finding.findingRef}`,
);
const initialized = ref(false);
const submittedRequestId = ref<string | null>(null);

function promote() {
  if (!showPromotionAction.value || props.busy) return;
  emits("promote");
}

// Keep the draft on dismissal or request failure. A successful patch resets it.
watch(open, (value) => {
  if (value && !initialized.value) {
    disposition.value = triage.value?.disposition ?? "FalsePositive";
    reason.value = triage.value?.currentReason ?? "";
    initialized.value = true;
    editing.value = !triage.value;
  }
});
watch(
  () => props.successVersion,
  (requestId) => {
    if (!requestId || requestId !== submittedRequestId.value) return;
    submittedRequestId.value = null;
    open.value = false;
    initialized.value = false;
    editing.value = false;
    reason.value = "";
    disposition.value = "FalsePositive";
  },
);
function save() {
  if (props.busy) return;
  submittedRequestId.value = crypto.randomUUID();
  emits(
    "save",
    disposition.value,
    reason.value.trim(),
    submittedRequestId.value,
  );
}
function cancel() {
  open.value = false;
  initialized.value = false;
  editing.value = false;
}
function clear() {
  if (props.busy) return;
  submittedRequestId.value = crypto.randomUUID();
  emits("clear", submittedRequestId.value);
}
</script>
