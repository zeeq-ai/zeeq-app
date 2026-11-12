<template>
  <UModal
    v-model:open="open"
    :title="`${revision.label || 'Saved revision'} · read only`"
    :ui="{ content: 'max-w-6xl' }"
  >
    <template #body>
      <div class="grid gap-3">
        <div class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted">
          <span
            >{{ revision.displayName }} · {{ revision.reviewFacet }} ·
            {{ revision.modelTier }}</span
          >
          <span>{{ formatDate(revision.createdAtUtc) }}</span>
          <span class="font-mono">{{ revision.contentHash }}</span>
        </div>
        <div class="min-h-96 overflow-hidden rounded-md border border-default">
          <CodeDiff
            :old-string="revisionText"
            :new-string="currentText"
            output-format="side-by-side"
            :theme="diffTheme"
            class="revision-diff"
          />
        </div>
      </div>
    </template>
    <template #footer>
      <div class="flex w-full justify-end">
        <UButton
          label="Close"
          color="neutral"
          variant="ghost"
          @click="open = false"
        />
      </div>
    </template>
  </UModal>
</template>

<script setup lang="ts">
import { CodeDiff as RawCodeDiff } from "v-code-diff";
import { defineComponent, h } from "vue";
import type {
  CodeReviewerAgentRevisionConfiguration,
  CodeReviewerAgentRevisionDto,
} from "@/api/generated";

const CodeDiff = defineComponent<{
  oldString: string;
  newString: string;
  outputFormat: "side-by-side" | "line-by-line";
  theme: "light" | "dark";
  class?: string;
}>(
  (props) => () =>
    h(RawCodeDiff, {
      "old-string": props.oldString,
      "new-string": props.newString,
      "output-format": props.outputFormat,
      theme: props.theme,
      class: props.class,
    }),
  { props: ["oldString", "newString", "outputFormat", "theme", "class"] },
);

const props = defineProps<{
  revision: CodeReviewerAgentRevisionDto;
  current: CodeReviewerAgentRevisionConfiguration;
}>();
const open = defineModel<boolean>("open", { required: true });
const colorMode = useColorMode();
const diffTheme = computed<"light" | "dark">(() =>
  colorMode.value === "dark" ? "dark" : "light",
);

const revisionText = computed(() => formatConfiguration(props.revision));
const currentText = computed(() => formatConfiguration(props.current));

function formatConfiguration(
  value: CodeReviewerAgentRevisionConfiguration | CodeReviewerAgentRevisionDto,
): string {
  const includedFiles = value.activationConfiguration.includedFiles ?? [];
  const excludedFiles = value.activationConfiguration.excludedFiles ?? [];
  return [
    `Display name: ${value.displayName}`,
    `Facet: ${value.reviewFacet}`,
    `Model tier: ${value.modelTier}`,
    `Status: ${value.enabled ? "Enabled" : "Disabled"}`,
    "",
    "Included files:",
    ...(includedFiles.length
      ? includedFiles.map((item) => `- ${item.matchType}: ${item.pattern}`)
      : ["- (none)"]),
    "",
    "Excluded files:",
    ...(excludedFiles.length
      ? excludedFiles.map((item) => `- ${item.matchType}: ${item.pattern}`)
      : ["- (none)"]),
    "",
    "## Prompt",
    "",
    value.prompt,
  ].join("\n");
}

function formatDate(value: Date | string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value instanceof Date ? value : new Date(value));
}
</script>

<style scoped>
.revision-diff {
  max-height: 28rem;
  overflow: auto;
}
</style>
