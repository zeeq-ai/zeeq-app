<template>
  <!-- Presentational panel: which KB documents/snippets the reviewers consulted,
       plus tool usage and content-gap misses. Renders nothing when there is no
       telemetry content so a clean review with an empty payload stays quiet. -->
  <div v-if="hasContent" class="mt-4 grid gap-3 border-t border-default pt-4">
    <!-- Summary line with roll-up counts. -->
    <div class="flex flex-wrap items-center gap-2">
      <UIcon name="i-hugeicons-book-01" class="size-4 text-muted" />
      <span class="text-sm font-semibold text-highlighted">
        Sources consulted
      </span>
      <UBadge
        :label="`${summary.documentCount} docs`"
        color="neutral"
        variant="subtle"
        size="sm"
        class="rounded-full"
      />
      <UBadge
        v-if="summary.snippetCount > 0"
        :label="`${summary.snippetCount} snippets`"
        color="neutral"
        variant="subtle"
        size="sm"
        class="rounded-full"
      />
      <UBadge
        v-if="summary.toolCallCount > 0"
        :label="`${summary.toolCallCount} tool calls`"
        color="neutral"
        variant="subtle"
        size="sm"
        class="rounded-full"
      />
    </div>

    <!-- Documents consulted, ranked by importance; collapsed by default. -->
    <UCollapsible v-if="documents.length > 0" v-model:open="documentsOpen">
      <UButton
        :label="`Documents consulted (${documents.length})`"
        :trailing-icon="
          documentsOpen
            ? 'i-hugeicons-minus-sign-square'
            : 'i-hugeicons-add-square'
        "
        color="neutral"
        variant="soft"
        block
        class="justify-between"
      />

      <template #content>
        <div class="mt-2 grid gap-2">
          <!-- One consulted document; expandable to its surfaced snippets. -->
          <div
            v-for="doc in documents"
            :key="doc.key"
            class="rounded-md border border-default bg-elevated/20"
          >
            <div
              class="flex flex-wrap items-center gap-2 px-3 py-2 cursor-pointer select-none"
              @click="toggleDocument(doc.key)"
            >
              <UBadge
                v-if="doc.isTopHit"
                label="Top hit"
                color="primary"
                variant="subtle"
                size="sm"
                class="rounded-full"
              />
              <span
                class="min-w-0 truncate text-sm font-medium text-highlighted"
              >
                {{ doc.title }}
              </span>
              <UBadge
                :label="doc.library"
                color="neutral"
                variant="outline"
                size="sm"
                class="rounded-full"
              />
              <span class="min-w-0 truncate font-mono text-xs text-muted">
                {{ doc.path }}
              </span>

              <div class="ml-auto flex shrink-0 items-center gap-2">
                <UTooltip v-if="doc.readAfterSearch" text="Read after search">
                  <UIcon
                    name="i-hugeicons-book-open-01"
                    class="size-4 text-muted"
                  />
                </UTooltip>
                <UBadge
                  :label="`${doc.hitCount} hits`"
                  color="neutral"
                  variant="subtle"
                  size="sm"
                  class="rounded-full"
                />
                <UIcon
                  :name="
                    isDocumentOpen(doc.key)
                      ? 'i-hugeicons-minus-sign-square'
                      : 'i-hugeicons-add-square'
                  "
                  class="size-4 text-muted"
                />
              </div>
            </div>

            <!-- Reviewer facets that surfaced this document. -->
            <div
              v-if="doc.facets.length > 0"
              class="flex flex-wrap gap-1 px-3 pb-2"
            >
              <UBadge
                v-for="facet in doc.facets"
                :key="facet"
                :label="facet"
                color="neutral"
                variant="soft"
                size="sm"
                class="rounded-full"
              />
            </div>

            <!-- Snippets surfaced within this document. -->
            <UCollapsible
              :open="isDocumentOpen(doc.key)"
              @update:open="setDocumentOpen(doc.key, $event)"
            >
              <template #content>
                <div
                  v-if="doc.snippets.length > 0"
                  class="grid gap-2 border-t border-default px-3 py-2"
                >
                  <div
                    v-for="snippet in doc.snippets"
                    :key="snippet.key"
                    class="flex flex-wrap items-center gap-2"
                  >
                    <UBadge
                      :label="snippet.kind"
                      :color="snippet.kindColor"
                      variant="subtle"
                      size="sm"
                      class="rounded-full"
                    />
                    <UBadge
                      v-if="snippet.language"
                      :label="snippet.language"
                      color="neutral"
                      variant="outline"
                      size="sm"
                      class="rounded-full"
                    />
                    <span class="min-w-0 flex-1 truncate text-sm text-default">
                      {{ snippet.heading }}
                    </span>
                    <UBadge
                      v-if="snippet.isTopHit"
                      label="Top hit"
                      color="primary"
                      variant="subtle"
                      size="sm"
                      class="rounded-full"
                    />
                    <UBadge
                      :label="`${snippet.hitCount} hits`"
                      color="neutral"
                      variant="subtle"
                      size="sm"
                      class="rounded-full"
                    />
                  </div>
                </div>

                <p
                  v-else
                  class="border-t border-default px-3 py-2 text-xs text-muted"
                >
                  Surfaced at the document level; no individual snippets
                  recorded.
                </p>
              </template>
            </UCollapsible>
          </div>
        </div>
      </template>
    </UCollapsible>

    <!-- Content gaps: searches that returned nothing (the actionable signal). -->
    <div
      v-if="missedQueries.length > 0"
      class="grid gap-1 rounded-md bg-warning/10 px-3 py-2"
    >
      <div class="flex flex-wrap items-center gap-2">
        <UIcon name="i-hugeicons-search-01" class="size-4 text-warning" />
        <span class="text-sm font-medium text-highlighted">Content gaps</span>
        <span class="text-xs text-muted">Searches that returned nothing.</span>
      </div>

      <ul class="grid gap-1">
        <li
          v-for="miss in missedQueries"
          :key="miss.key"
          class="flex flex-wrap items-center gap-2 text-sm"
        >
          <span class="font-mono text-xs text-default">{{ miss.query }}</span>
          <UBadge
            :label="miss.tool"
            color="neutral"
            variant="outline"
            size="sm"
            class="rounded-full"
          />
        </li>
      </ul>
    </div>

    <!-- Per-tool call/success/failure counts; collapsed by default. -->
    <UCollapsible v-if="tools.length > 0" v-model:open="toolsOpen">
      <UButton
        :label="`Tool usage (${tools.length})`"
        :trailing-icon="
          toolsOpen ? 'i-hugeicons-minus-sign-square' : 'i-hugeicons-add-square'
        "
        color="neutral"
        variant="ghost"
        size="sm"
        block
        class="justify-between"
      />

      <template #content>
        <UTable :data="tools" :columns="toolColumns" class="mt-1" />
      </template>
    </UCollapsible>
  </div>
</template>

<script setup lang="ts">
import type { CodeReviewSourceTelemetryDto } from "@/api/generated";

type BadgeColor = "primary" | "neutral";

type SnippetViewModel = {
  key: string;
  heading: string;
  kind: string;
  kindColor: BadgeColor;
  language: string | null;
  hitCount: number;
  bestRank: number;
  isTopHit: boolean;
  facets: string[];
};

type DocumentViewModel = {
  key: string;
  title: string;
  path: string;
  library: string;
  hitCount: number;
  bestRank: number;
  isTopHit: boolean;
  readAfterSearch: boolean;
  facets: string[];
  snippets: SnippetViewModel[];
};

/**
 * Receives the readable source-telemetry projection from the accordion. No store
 * access: the parent already deserialized the payload once and pushes it down.
 */
const props = defineProps<{ sourceTelemetry: CodeReviewSourceTelemetryDto }>();

/** Local collapse state; documents and tool usage start closed to stay compact. */
const documentsOpen = ref(false);
const toolsOpen = ref(false);
const openDocumentByKey = ref<Record<string, boolean>>({});

/**
 * Roll-up counts, coerced to numbers because generated integer fields can arrive
 * as strings.
 */
const summary = computed(() => ({
  documentCount: toNumber(props.sourceTelemetry.summary.documentCount),
  snippetCount: toNumber(props.sourceTelemetry.summary.snippetCount),
  toolCallCount: toNumber(props.sourceTelemetry.summary.toolCallCount),
  missedQueryCount: toNumber(props.sourceTelemetry.summary.missedQueryCount),
}));

/** Documents already ordered by importance server-side; mapped to a view model. */
const documents = computed<DocumentViewModel[]>(() =>
  (props.sourceTelemetry.documents ?? []).map((doc, index) =>
    toDocumentViewModel(doc, index),
  ),
);

/** Tool usage rows for the counts table, coerced to numbers. */
const tools = computed(() =>
  (props.sourceTelemetry.toolUsage ?? []).map((tool) => ({
    tool: tool.tool,
    calls: toNumber(tool.calls),
    succeeded: toNumber(tool.succeeded),
    failed: toNumber(tool.failed),
  })),
);

/** Content-gap misses with a stable key for the list :key binding. */
const missedQueries = computed(() =>
  (props.sourceTelemetry.missedQueries ?? []).map((miss, index) => ({
    key: `${miss.tool}:${miss.query}:${index}`,
    query: miss.query,
    tool: miss.tool,
  })),
);

/** Whether there is anything worth rendering; keeps empty payloads invisible. */
const hasContent = computed(
  () =>
    documents.value.length > 0 ||
    tools.value.length > 0 ||
    missedQueries.value.length > 0,
);

const toolColumns = [
  { accessorKey: "tool", header: "Tool" },
  { accessorKey: "calls", header: "Calls" },
  { accessorKey: "succeeded", header: "Succeeded" },
  { accessorKey: "failed", header: "Failed" },
];

/** Reads whether one document's snippet list is expanded (default collapsed). */
function isDocumentOpen(key: string): boolean {
  return openDocumentByKey.value[key] === true;
}

/** Sets the controlled UCollapsible state for one document row. */
function setDocumentOpen(key: string, open: boolean): void {
  openDocumentByKey.value = { ...openDocumentByKey.value, [key]: open };
}

/** Toggles a document's snippet list from the header click. */
function toggleDocument(key: string): void {
  setDocumentOpen(key, !isDocumentOpen(key));
}

/**
 * Builds a document view model, deriving the top-hit marker from the best rank
 * and precomputing snippet view models so the template stays declarative.
 */
function toDocumentViewModel(
  doc: CodeReviewSourceTelemetryDto["documents"][number],
  index: number,
): DocumentViewModel {
  const bestRank = toNumber(doc.bestRank);

  return {
    key: `${doc.documentId || doc.path}:${index}`,
    title: doc.title || doc.path,
    path: doc.path,
    library: doc.library,
    hitCount: toNumber(doc.hitCount),
    bestRank,
    isTopHit: bestRank === 1,
    readAfterSearch: doc.readAfterSearch,
    facets: doc.facets ?? [],
    snippets: (doc.snippets ?? []).map((snippet, snippetIndex) =>
      toSnippetViewModel(snippet, snippetIndex),
    ),
  };
}

/** Builds a snippet view model, coloring code samples distinctly from sections. */
function toSnippetViewModel(
  snippet: CodeReviewSourceTelemetryDto["documents"][number]["snippets"][number],
  index: number,
): SnippetViewModel {
  const bestRank = toNumber(snippet.bestRank);

  return {
    key: `${snippet.snippetId || snippet.heading}:${index}`,
    heading: snippet.heading,
    kind: snippet.kind,
    kindColor: snippet.kind === "CodeSample" ? "primary" : "neutral",
    language: snippet.language,
    hitCount: toNumber(snippet.hitCount),
    bestRank,
    isTopHit: bestRank === 1,
    facets: snippet.facets ?? [],
  };
}

/**
 * Normalizes generated numeric fields that may arrive as strings into safe
 * numbers for comparison and display.
 */
function toNumber(value: number | string): number {
  return typeof value === "number" ? value : Number(value) || 0;
}
</script>
