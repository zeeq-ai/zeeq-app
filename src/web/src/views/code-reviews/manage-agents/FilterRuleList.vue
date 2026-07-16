<template>
  <div class="grid gap-2">
    <div class="flex items-center justify-between gap-2">
      <div>
        <h4 class="text-sm font-bold text-highlighted">{{ title }}</h4>
        <p class="text-xs text-muted">{{ emptyDescription }}</p>
      </div>
      <UButton
        label="Add"
        icon="i-hugeicons-plus-sign"
        size="xs"
        color="neutral"
        variant="soft"
        :disabled
        @click="emits('add')"
      />
    </div>

    <div
      v-if="rules.length === 0"
      class="rounded-md border border-dashed border-default p-3 text-sm text-muted"
    >
      No rules configured.
    </div>

    <div v-else class="grid gap-2">
      <div
        v-for="(rule, index) in rules"
        :key="index"
        class="grid gap-2 md:grid-cols-[11rem_minmax(0,1fr)_auto] md:items-end"
      >
        <UFormField :label="index === 0 ? 'Match' : undefined">
          <USelect
            :model-value="rule.matchType"
            :items="matchTypeItems"
            color="neutral"
            :disabled
            class="w-full"
            @update:model-value="updateMatchType(index, $event)"
          />
        </UFormField>

        <UFormField :label="index === 0 ? 'Pattern' : undefined">
          <UInput
            :model-value="rule.pattern"
            placeholder="src/backend/"
            :disabled
            class="w-full"
            @update:model-value="updatePattern(index, $event)"
          />
        </UFormField>

        <UButton
          icon="i-hugeicons-delete-02"
          color="neutral"
          variant="ghost"
          square
          :disabled
          @click="emits('remove', index)"
        />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import {
  codeReviewFileNameMatchTypeEnum,
  type CodeReviewFileMatchCriteriaDto,
  type CodeReviewFileNameMatchType,
} from "@/api/generated";
import { matchTypeItems } from "@/stores/code-review-store";

const props = defineProps<{
  title: string;
  emptyDescription: string;
  rules: CodeReviewFileMatchCriteriaDto[];
  disabled: boolean;
}>();

const emits = defineEmits<{
  add: [];
  remove: [index: number];
  update: [index: number, rule: CodeReviewFileMatchCriteriaDto];
}>();

/** Updates the match operation while preserving the current pattern. */
function updateMatchType(index: number, value: string) {
  const matchType = parseMatchType(value);
  const current = props.rules[index];

  if (!current) {
    return;
  }

  emits("update", index, {
    matchType,
    pattern: current.pattern,
  });
}

/** Updates the pattern while preserving the current match operation. */
function updatePattern(index: number, value: string) {
  const current = props.rules[index];

  if (!current) {
    return;
  }

  emits("update", index, {
    matchType: current.matchType,
    pattern: value,
  });
}

function parseMatchType(value: string): CodeReviewFileNameMatchType {
  if (value === codeReviewFileNameMatchTypeEnum.ExactPath) {
    return codeReviewFileNameMatchTypeEnum.ExactPath;
  }

  if (value === codeReviewFileNameMatchTypeEnum.Extension) {
    return codeReviewFileNameMatchTypeEnum.Extension;
  }

  if (value === codeReviewFileNameMatchTypeEnum.Glob) {
    return codeReviewFileNameMatchTypeEnum.Glob;
  }

  return codeReviewFileNameMatchTypeEnum.PathPrefix;
}
</script>
