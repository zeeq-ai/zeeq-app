<template>
  <!--
  "Preview parse" slideover: shows what the parse + snippet-indexing pipeline
  would extract from one document — title, keywords, headings, and the
  composed section/code snippets — without persisting anything. Read-only
  workbench, same visual language as DocumentSearchPanel's "Test search"
  (UCard rows, badges) but scoped to a single document instead of a query.
  -->
  <USlideover
    v-model:open="open"
    side="right"
    title="Preview parse"
    :ui="{ content: 'max-w-2xl' }"
  >
    <template #body>
      <div v-if="loading" class="flex justify-center py-8">
        <UIcon
          name="i-hugeicons-loading-03"
          class="size-6 animate-spin text-neutral-400"
        />
      </div>

      <UEmpty
        v-else-if="!preview"
        title="No preview"
        description="Open a document and click Preview parse to see it here."
        icon="i-hugeicons-search-list-01"
      />

      <div v-else class="flex flex-col gap-4">
        <!-- Path + title -->
        <div class="flex flex-col gap-1.5">
          <div class="flex items-center gap-1.5 text-xs text-neutral-500">
            <UIcon name="i-hugeicons-hierarchy-files" class="size-3" />
            <span class="font-mono truncate">{{ preview.path }}</span>
          </div>
          <span class="font-medium text-sm">{{ preview.title }}</span>
        </div>

        <!-- Keywords -->
        <div v-if="preview.keywords.length > 0" class="flex flex-wrap gap-1">
          <UBadge
            v-for="keyword in preview.keywords"
            :key="keyword"
            :label="keyword"
            color="primary"
            variant="soft"
            size="md"
          />
        </div>

        <!-- Headings -->
        <UFormField
          v-if="preview.headings.length > 0"
          label="Headings"
          size="sm"
        >
          <div class="flex flex-wrap gap-1">
            <UBadge
              v-for="(heading, idx) in preview.headings"
              :key="idx"
              :label="heading"
              color="neutral"
              variant="subtle"
              size="sm"
            />
          </div>
        </UFormField>

        <!-- Sections / Code snippet tabs -->
        <UTabs v-model="mode" :items="modeItems" :content="false" size="sm" />

        <UEmpty
          v-if="currentSnippets.length === 0"
          title="No snippets"
          :description="emptySnippetsDescription"
          icon="i-hugeicons-search-list-01"
        />

        <div v-else class="flex flex-col gap-2">
          <SnippetContentCard
            v-for="(snippet, idx) in currentSnippets"
            :key="idx"
            :heading-path="snippet.headingPath"
            :content="snippet.content"
            :is-code="snippet.kind === 'code'"
            :language="snippet.language"
          >
            <template #badges>
              <UBadge
                :label="`${snippet.tokenCount} tokens`"
                color="neutral"
                variant="soft"
                size="sm"
              />
              <UBadge
                v-if="snippet.language"
                :label="snippet.language"
                color="info"
                variant="soft"
                size="sm"
              />
              <UBadge
                v-if="snippet.tag"
                :label="snippet.tag"
                color="neutral"
                variant="soft"
                size="sm"
              />
              <UBadge
                v-for="identifier in snippet.identifiers"
                :key="identifier"
                :label="identifier"
                color="success"
                variant="soft"
                size="sm"
              />
            </template>
          </SnippetContentCard>
        </div>
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import SnippetContentCard from "@/views/libraries/SnippetContentCard.vue";
import type { DocumentParsePreviewResponse } from "@/api/generated/types/DocumentParsePreviewResponse";

type SnippetMode = "sections" | "code";

const props = defineProps<{
  preview: DocumentParsePreviewResponse | null;
  loading: boolean;
}>();

const open = defineModel<boolean>("open", { required: true });

const modeItems = [
  { label: "Sections", value: "sections" },
  { label: "Code", value: "code" },
];

const mode = ref<SnippetMode>("sections");

const currentSnippets = computed(() => {
  const kind = mode.value === "code" ? "code" : "section";
  return props.preview?.snippets.filter((s) => s.kind === kind) ?? [];
});

const emptySnippetsDescription = computed(() =>
  mode.value === "code"
    ? "No fenced code blocks would be composed from this document."
    : "No sections are long enough to be composed as a section snippet.",
);
</script>
