<template>
  <!--
  "Test" search panel: a slideover with a mode toggle (Documents / Sections /
  Code), a query input, and collapsible results.
  Documents mode: title, library + path, keyword badges, and match hints
  (D-1: library name read from each result row, not from a prop).
  Sections/Code modes: heading-path breadcrumb, content preview (code fenced,
  section truncated prose), source path/title, and RRF score components — this
  is a tuning workbench, not a polished end-user surface, so ranking internals
  are deliberately visible.
  -->
  <USlideover
    v-model:open="open"
    side="right"
    title="Test search"
    :ui="{ content: 'max-w-2xl' }"
  >
    <template #body>
      <div class="flex flex-col gap-4">
        <UTabs v-model="mode" :items="modeItems" :content="false" size="sm" />

        <!-- Search input -->
        <UInput
          v-model="query"
          :placeholder="queryPlaceholder"
          icon="i-hugeicons-search-01"
          :loading="isSearching"
          @keyup.enter="onSearch"
        >
          <template #trailing>
            <UButton
              icon="i-hugeicons-search-01"
              size="xs"
              color="primary"
              variant="ghost"
              :disabled="!query.trim() || isSearching"
              @click="onSearch"
            />
          </template>
        </UInput>

        <!-- Exclude-paths chip input (Sections/Code modes only) -->
        <UFormField
          v-if="mode !== 'documents'"
          label="Exclude document paths"
          size="sm"
        >
          <UInputTags
            v-model="excludePaths"
            placeholder="/docs/already-read.md"
            class="w-full"
            addOnBlur
            addOnPaste
            addOnTab
            delimiter=","
            :max="3"
          />
        </UFormField>

        <!-- Degraded-mode banner (Sections/Code modes only) -->
        <UAlert
          v-if="mode !== 'documents' && snippetDegraded"
          title="Semantic ranking unavailable"
          description="The embedding provider was unreachable or timed out; results are full-text only."
          icon="i-hugeicons-alert-02"
          color="warning"
          variant="subtle"
        />

        <!-- Results list -->
        <div v-if="isSearching" class="flex justify-center py-8">
          <UIcon
            name="i-hugeicons-loading-03"
            class="size-6 animate-spin text-neutral-400"
          />
        </div>

        <div v-else-if="currentResultCount === 0 && hasSearched" class="py-8">
          <UEmpty
            title="No results"
            description="Try different keywords."
            icon="i-hugeicons-search-01"
          />
        </div>

        <!-- Documents mode: each row shows its own library name (D-1) -->
        <div v-else-if="mode === 'documents'" class="flex flex-col gap-2">
          <UCard v-for="(result, idx) in results" :key="idx" variant="soft">
            <div class="flex flex-col gap-1.5">
              <!-- Title + match hint -->
              <div class="flex items-center justify-between gap-2">
                <span class="font-medium text-sm truncate">
                  {{ result.title || "Untitled" }}
                </span>
                <span class="text-xs text-neutral-400 shrink-0">
                  {{ matchHint(result) }}
                </span>
              </div>

              <!-- Library + path (D-1: library from each row) -->
              <div class="flex items-center gap-1.5 text-xs text-neutral-500">
                <UIcon name="i-hugeicons-hierarchy-files" class="size-3" />
                <span class="font-mono"
                  >{{ result.library }}{{ result.path }}</span
                >
              </div>

              <!-- Keyword badges -->
              <div
                v-if="result.keywords.length > 0"
                class="flex flex-wrap gap-1"
              >
                <UBadge
                  v-for="kw in result.keywords"
                  :key="kw"
                  :label="kw"
                  color="primary"
                  variant="soft"
                  size="md"
                />
              </div>
            </div>
          </UCard>
        </div>

        <!-- Sections/Code mode: heading breadcrumb, content preview, score components -->
        <div v-else class="flex flex-col gap-2">
          <SnippetContentCard
            v-for="(result, idx) in snippetResults"
            :key="idx"
            :document-path="result.documentPath"
            :heading-path="result.headingPath"
            :content="result.content"
            :is-code="mode === 'code'"
            :language="result.language"
          >
            <template #badges>
              <UBadge
                :label="`score ${Number(result.score).toFixed(3)}`"
                color="primary"
                variant="soft"
                size="sm"
              />
              <UBadge
                :label="rankLabel('vector', result.vectorRank)"
                color="neutral"
                variant="soft"
                size="sm"
              />
              <UBadge
                :label="rankLabel('text', result.textRank)"
                color="neutral"
                variant="soft"
                size="sm"
              />
              <UBadge
                v-if="result.identifierMatch"
                label="identifier match"
                color="success"
                variant="soft"
                size="sm"
              />
              <UBadge
                v-if="result.language"
                :label="result.language"
                color="info"
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
import type { DocumentSearchResultResponse } from "@/api/generated/types/DocumentSearchResultResponse";
import type { SnippetSearchResultResponse } from "@/api/generated/types/SnippetSearchResultResponse";

type SearchMode = "documents" | "sections" | "code";

const props = defineProps<{
  results: DocumentSearchResultResponse[];
  searching: boolean;
  snippetResultsByKind: {
    section: SnippetSearchResultResponse[];
    code: SnippetSearchResultResponse[];
  };
  snippetSearching: boolean;
}>();

const open = defineModel<boolean>("open", { required: true });

const emits = defineEmits<{
  search: [query: string];
  snippetSearch: [
    query: string,
    kind: "section" | "code",
    excludeDocumentPaths: string[],
  ];
}>();

const modeItems = [
  { label: "Documents", value: "documents" },
  { label: "Sections", value: "sections" },
  { label: "Code", value: "code" },
];

const mode = ref<SearchMode>("documents");
const query = ref("");
const excludePaths = ref<string[]>([]);
const hasSearched = ref(false);

// Switching modes should not show a stale "No results" state for a mode that
// has not been searched yet in this panel session.
watch(mode, () => {
  hasSearched.value = false;
});

const isSearching = computed(() =>
  mode.value === "documents" ? props.searching : props.snippetSearching,
);

// Sections and Code each read from their own kind-scoped array in the store
// (snippetSearchResultsByKind), so switching modes without re-searching can
// never display stale rows from the other kind — the freshness guarantee is
// structural, not tracked here.
const snippetResults = computed(() =>
  mode.value === "code"
    ? props.snippetResultsByKind.code
    : props.snippetResultsByKind.section,
);

const currentResultCount = computed(() =>
  mode.value === "documents"
    ? props.results.length
    : snippetResults.value.length,
);

/** True when the degraded flag is set on any row of the current mode's snippet search. */
const snippetDegraded = computed(() =>
  snippetResults.value.some((result) => result.degraded),
);

const queryPlaceholder = computed(() => {
  switch (mode.value) {
    case "sections":
      return "Search document sections...";
    case "code":
      return "Search code snippets...";
    default:
      return "Search documents...";
  }
});

/** Triggers the mode-appropriate search and marks that a search has been performed. */
function onSearch() {
  const trimmed = query.value.trim();
  if (!trimmed) return;

  hasSearched.value = true;

  if (mode.value === "documents") {
    emits("search", trimmed);
    return;
  }

  emits(
    "snippetSearch",
    trimmed,
    mode.value === "code" ? "code" : "section",
    excludePaths.value,
  );
}

/**
 * Builds a human-readable match hint from the score data.
 * e.g. "Full-text (0.85)" or "Fuzzy (0.42)"
 */
function matchHint(result: DocumentSearchResultResponse): string {
  const type = result.matchType;
  const score =
    type === "FullText" || type === "Both"
      ? Number(result.fullTextScore).toFixed(2)
      : Number(result.fuzzyScore).toFixed(2);

  return `${type} (${score})`;
}

/**
 * Formats a vector/text arm rank badge. A rank of 0 means the row was not a hit in that
 * arm at all (see SnippetSearchRow.VectorRank/TextRank), not literal rank zero — showing
 * "vector #0" would read as if it were the best-ranked hit, so this reads "no match" instead.
 */
function rankLabel(arm: "vector" | "text", rank: number | string): string {
  return Number(rank) === 0 ? `${arm}: no match` : `${arm} #${rank}`;
}
</script>
