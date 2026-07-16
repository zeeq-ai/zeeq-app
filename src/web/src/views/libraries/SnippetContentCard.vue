<template>
  <!--
  Shared snippet-preview card: document-path breadcrumb (optional), heading-path
  breadcrumb, and a content preview (Comark-highlighted code or plain-text
  section prose). Used by both the "Test search" Sections/Code results
  (DocumentSearchPanel.vue) and the "Preview parse" slideover
  (DocumentParsePreviewSlideover.vue) — the two differ only in which metadata
  badges follow the content, which the caller supplies via the #badges slot.
  -->
  <UCard variant="soft">
    <div class="flex flex-col gap-1.5">
      <div
        v-if="documentPath"
        class="flex items-center gap-1.5 text-xs text-neutral-500"
      >
        <UIcon name="i-hugeicons-hierarchy-files" class="size-3" />
        <span class="font-mono truncate">{{ documentPath }}</span>
      </div>

      <div class="text-sm font-medium truncate">{{ headingPath }}</div>

      <Comark
        v-if="isCode"
        :markdown="toFencedMarkdown(content, language)"
        :plugins="codeHighlightPlugins"
        class="text-xs"
      />
      <p v-else class="text-xs text-neutral-600 line-clamp-4">
        {{ content }}
      </p>

      <div class="flex flex-wrap items-center gap-1.5">
        <slot name="badges" />
      </div>
    </div>
  </UCard>
</template>

<script setup lang="ts">
import { Comark } from "@comark/vue";
import { useCodeHighlight } from "@/composables/useCodeHighlight";

defineProps<{
  /** Owning document's path, shown as a breadcrumb above the heading. Omit when the card is
   * already scoped to one document (e.g. the parse-preview slideover). */
  documentPath?: string | null;
  headingPath: string;
  content: string;
  isCode: boolean;
  language?: string | null;
}>();

const { codeHighlightPlugins, toFencedMarkdown } = useCodeHighlight();
</script>
