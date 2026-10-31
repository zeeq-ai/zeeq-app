<template>
  <section class="min-w-0 rounded-md border border-default p-3">
    <div class="space-y-2">
      <div class="min-w-0">
        <p class="text-xs text-muted">{{ sideLabel }}</p>
      </div>
      <div class="flex flex-wrap gap-1">
        <UButton
          label="Copy"
          :aria-label="`Copy ${sideLabel.toLowerCase()}`"
          icon="i-hugeicons-copy-01"
          color="neutral"
          variant="ghost"
          size="xs"
          @click="emits('copy', snapshot)"
        />
        <UButton
          label="Download"
          :aria-label="`Download ${sideLabel.toLowerCase()}`"
          icon="i-hugeicons-download-04"
          color="neutral"
          variant="ghost"
          size="xs"
          @click="emits('download', snapshot)"
        />
      </div>
    </div>
    <p v-if="previewLimited" class="mt-3 text-xs text-muted">
      Preview limited to
      {{ previewCharacterLimit.toLocaleString() }} characters. Copy and download
      include the full version.
    </p>
    <pre
      class="mt-3 max-h-48 overflow-auto whitespace-pre-wrap break-words rounded bg-elevated/30 p-2 font-mono text-xs"
      >{{ previewContent }}</pre>
  </section>
</template>

<script setup lang="ts">
import type { AuditHistoryDocumentSnapshot } from "./audit-history-types";

const props = defineProps<{
  side: "before" | "after";
  snapshot: AuditHistoryDocumentSnapshot;
}>();

const emits = defineEmits<{
  copy: [snapshot: AuditHistoryDocumentSnapshot];
  download: [snapshot: AuditHistoryDocumentSnapshot];
}>();

const sideLabel = computed(() =>
  props.side === "before" ? "Before this change" : "After this change",
);
const previewCharacterLimit = 10_000;
const previewLimited = computed(
  () => props.snapshot.content.length > previewCharacterLimit,
);
const previewContent = computed(() =>
  previewLimited.value
    ? `${props.snapshot.content.slice(0, previewCharacterLimit)}\n\n… preview truncated …`
    : props.snapshot.content,
);
</script>
