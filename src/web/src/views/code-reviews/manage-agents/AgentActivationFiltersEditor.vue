<template>
  <div class="grid gap-4">
    <!-- Included files narrow activation; empty means all files are eligible. -->
    <FilterRuleList
      title="Included files"
      empty-description="Empty means every repository-scoped file can match."
      :rules="includedFiles"
      :disabled
      @add="addIncluded"
      @remove="removeIncluded"
      @update="updateIncluded"
    />

    <USeparator />

    <!-- Excluded files always win over includes. -->
    <FilterRuleList
      title="Excluded files"
      empty-description="Excluded rules always win over included rules."
      :rules="excludedFiles"
      :disabled
      @add="addExcluded"
      @remove="removeExcluded"
      @update="updateExcluded"
    />
  </div>
</template>

<script setup lang="ts">
import type { CodeReviewFileMatchCriteriaDto } from "@/api/generated";
import { codeReviewFileNameMatchTypeEnum } from "@/api/generated";
import FilterRuleList from "./FilterRuleList.vue";

const props = defineProps<{
  includedFiles: CodeReviewFileMatchCriteriaDto[];
  excludedFiles: CodeReviewFileMatchCriteriaDto[];
  disabled: boolean;
}>();

const emits = defineEmits<{
  update: [
    value: {
      includedFiles: CodeReviewFileMatchCriteriaDto[];
      excludedFiles: CodeReviewFileMatchCriteriaDto[];
    },
  ];
}>();

/** Adds a blank include rule using the most common path-prefix matcher. */
function addIncluded() {
  emits("update", {
    includedFiles: [...props.includedFiles, blankRule()],
    excludedFiles: props.excludedFiles,
  });
}

/** Adds a blank exclude rule using the most common path-prefix matcher. */
function addExcluded() {
  emits("update", {
    includedFiles: props.includedFiles,
    excludedFiles: [...props.excludedFiles, blankRule()],
  });
}

function removeIncluded(index: number) {
  emits("update", {
    includedFiles: removeAt(props.includedFiles, index),
    excludedFiles: props.excludedFiles,
  });
}

function removeExcluded(index: number) {
  emits("update", {
    includedFiles: props.includedFiles,
    excludedFiles: removeAt(props.excludedFiles, index),
  });
}

function updateIncluded(index: number, rule: CodeReviewFileMatchCriteriaDto) {
  emits("update", {
    includedFiles: replaceAt(props.includedFiles, index, rule),
    excludedFiles: props.excludedFiles,
  });
}

function updateExcluded(index: number, rule: CodeReviewFileMatchCriteriaDto) {
  emits("update", {
    includedFiles: props.includedFiles,
    excludedFiles: replaceAt(props.excludedFiles, index, rule),
  });
}

function blankRule(): CodeReviewFileMatchCriteriaDto {
  return {
    matchType: codeReviewFileNameMatchTypeEnum.PathPrefix,
    pattern: "",
  };
}

function removeAt(
  rules: CodeReviewFileMatchCriteriaDto[],
  index: number,
): CodeReviewFileMatchCriteriaDto[] {
  return rules.filter((_, itemIndex) => itemIndex !== index);
}

function replaceAt(
  rules: CodeReviewFileMatchCriteriaDto[],
  index: number,
  rule: CodeReviewFileMatchCriteriaDto,
): CodeReviewFileMatchCriteriaDto[] {
  return rules.map((item, itemIndex) => (itemIndex === index ? rule : item));
}
</script>
