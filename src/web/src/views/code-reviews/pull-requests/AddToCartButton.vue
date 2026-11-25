<template>
  <!-- Already in the active cart draft: click removes immediately. -->
  <UTooltip
    v-if="isInCart"
    :text="triaged ? 'Triaged after add' : 'Remove from cart'"
    ><UButton
      icon="i-hugeicons-shopping-basket-remove-01"
      label="In cart"
      color="primary"
      variant="soft"
      size="xs"
      :loading="isToggling"
      @click="emits('toggleCart', finding, reviewer, null)"
  /></UTooltip>

  <!-- Not yet added: opens the annotate popover before confirming. -->
  <UTooltip
    v-else
    :text="triaged ? 'Triaged — Clear to add to fix' : 'Add to fix'"
    ><UPopover v-model:open="popoverOpen">
      <UButton
        icon="i-hugeicons-shopping-basket-add-01"
        label="Add to fix"
        color="neutral"
        variant="outline"
        size="xs"
        :loading="isToggling"
        :disabled="triaged"
      />

      <template #content>
        <div class="grid w-80 gap-2 p-3">
          <UTextarea
            v-model="annotationDraft"
            placeholder="Add a note for the agent (optional)"
            :maxlength="500"
            :rows="3"
          />
          <div class="flex items-center justify-between">
            <span class="text-xs text-muted">
              {{ annotationDraft.length }}/500
            </span>
            <UButton
              label="Save"
              color="primary"
              variant="subtle"
              size="xs"
              @click="handleAdd"
            />
          </div>
        </div>
      </template> </UPopover
  ></UTooltip>
</template>

<script setup lang="ts">
import type {
  CodeReviewFindingDto,
  CodeReviewReviewerFindingsDto,
} from "@/api/generated";

const props = defineProps<{
  finding: CodeReviewFindingDto;
  reviewer: CodeReviewReviewerFindingsDto;
  isInCart: boolean;
  isToggling: boolean;
}>();

const emits = defineEmits<{
  toggleCart: [
    finding: CodeReviewFindingDto,
    reviewer: CodeReviewReviewerFindingsDto,
    annotation: string | null,
  ];
}>();

const triaged = computed(
  () => props.finding.triage?.approvalState === "Confirmed",
);
const popoverOpen = ref(false);

const annotationDraft = ref("");

function handleAdd() {
  popoverOpen.value = false;
  emits(
    "toggleCart",
    props.finding,
    props.reviewer,
    annotationDraft.value || null,
  );
  annotationDraft.value = "";
}
</script>
