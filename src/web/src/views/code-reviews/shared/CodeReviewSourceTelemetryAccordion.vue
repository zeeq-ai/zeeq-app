<template>
  <!-- Shared source telemetry renderer for persisted reviews and synthetic agent tests. -->
  <UAccordion
    v-if="accordionItems.length > 0"
    type="multiple"
    :default-value="[]"
    :items="accordionItems"
    :ui="{
      root: 'rounded-md border border-default bg-default',
      item: 'border-b border-default last:border-b-0',
      trigger: 'px-3 py-2.5 hover:bg-elevated/40',
      label: 'flex-1 min-w-0 text-sm font-medium text-highlighted',
      body: 'px-3 pb-3 pt-0',
    }"
  >
    <template #default="{ item }">
      <div class="flex min-w-0 w-full items-center gap-2">
        <span class="min-w-0 truncate">{{ item.label }}</span>
        <UBadge
          :label="`${item.count}`"
          color="neutral"
          variant="subtle"
          size="sm"
          class="ml-auto"
        />
      </div>
    </template>

    <template #body="{ item }">
      <div v-if="item.value === 'documents'" class="grid gap-2">
        <div
          v-for="document in documentRows"
          :key="document.value"
          class="grid min-w-0 gap-1 rounded-md bg-elevated/20 px-3 py-2"
        >
          <div class="flex min-w-0 items-center gap-2">
            <UIcon
              :name="document.icon"
              class="size-4 shrink-0"
              :class="document.iconClass"
            />
            <span class="min-w-0 truncate text-sm font-medium">
              {{ document.label }}
            </span>
            <div class="ml-auto flex shrink-0 items-center gap-1.5">
              <UBadge
                v-if="document.isTop"
                label="Top"
                color="primary"
                variant="soft"
                size="sm"
              />
              <UBadge
                v-if="document.snippetCountLabel"
                :label="document.snippetCountLabel"
                color="neutral"
                variant="soft"
                size="sm"
              />
            </div>
          </div>

          <p class="truncate font-mono text-xs text-muted">
            {{ document.description }}
          </p>
          <div
            v-if="document.facets.length > 0 || document.readAfterSearch"
            class="flex min-w-0 flex-wrap items-center gap-1.5"
          >
            <UBadge
              v-if="document.readAfterSearch"
              label="Read after search"
              color="primary"
              variant="soft"
              size="sm"
            />
            <UBadge
              v-for="facet in document.facets"
              :key="facet"
              :label="facet"
              color="neutral"
              variant="soft"
              size="sm"
            />
          </div>
        </div>
      </div>

      <div v-else-if="item.value === 'snippets'" class="grid gap-2">
        <div
          v-for="snippet in snippetRows"
          :key="snippet.value"
          class="grid min-w-0 gap-1 rounded-md bg-elevated/20 px-3 py-2"
        >
          <div class="flex min-w-0 items-center gap-2">
            <UIcon
              :name="snippet.icon"
              class="size-4 shrink-0"
              :class="snippet.iconClass"
            />
            <span class="min-w-0 truncate text-sm font-medium">
              {{ snippet.label }}
            </span>
            <div class="ml-auto flex shrink-0 items-center gap-1.5">
              <UBadge
                v-if="snippet.isTop"
                label="Top"
                color="primary"
                variant="soft"
                size="sm"
              />
              <UBadge
                :label="snippet.kindLabel"
                color="neutral"
                variant="soft"
                size="sm"
              />
            </div>
          </div>

          <p class="truncate font-mono text-xs text-muted">
            {{ snippet.description }}
          </p>
          <div
            v-if="snippet.facets.length > 0"
            class="flex min-w-0 flex-wrap items-center gap-1.5"
          >
            <UBadge
              v-for="facet in snippet.facets"
              :key="facet"
              :label="facet"
              color="neutral"
              variant="soft"
              size="sm"
            />
          </div>
        </div>
      </div>

      <div v-else-if="item.value === 'tools'" class="grid gap-2">
        <div
          v-for="tool in toolRows"
          :key="tool.value"
          class="flex min-w-0 items-center gap-2 rounded-md bg-elevated/20 px-3 py-2"
        >
          <UIcon
            name="i-hugeicons-wrench-01"
            class="size-4 shrink-0 text-muted"
          />
          <span class="min-w-0 flex-1 truncate font-mono text-sm">
            {{ tool.label }}
          </span>
          <UBadge
            :label="`${tool.calls} calls`"
            color="neutral"
            variant="soft"
            size="sm"
          />
          <UBadge
            v-if="tool.failed > 0"
            :label="`${tool.failed} failed`"
            color="error"
            variant="soft"
            size="sm"
          />
        </div>
      </div>

      <div v-else-if="item.value === 'content-gaps'" class="grid gap-2">
        <div
          v-for="contentGap in contentGapRows"
          :key="contentGap.value"
          class="grid min-w-0 gap-1 rounded-md bg-warning/10 px-3 py-2"
        >
          <div class="flex min-w-0 items-center gap-2">
            <UIcon
              name="i-hugeicons-search-01"
              class="size-4 shrink-0 text-warning"
            />
            <span class="min-w-0 flex-1 truncate font-mono text-sm">
              {{ contentGap.query }}
            </span>
            <UBadge
              :label="contentGap.tool"
              color="neutral"
              variant="soft"
              size="sm"
            />
          </div>
          <div
            v-if="contentGap.facets.length > 0"
            class="flex min-w-0 flex-wrap items-center gap-1.5"
          >
            <UBadge
              v-for="facet in contentGap.facets"
              :key="facet"
              :label="facet"
              color="neutral"
              variant="soft"
              size="sm"
            />
          </div>
        </div>
      </div>
    </template>
  </UAccordion>
</template>

<script setup lang="ts">
import type { CodeReviewSourceTelemetryDto } from "@/api/generated";

import {
  buildSourceTelemetryAccordionItems,
  buildSourceTelemetryContentGapRows,
  buildSourceTelemetryDocumentRows,
  buildSourceTelemetrySnippetRows,
  buildSourceTelemetryToolRows,
} from "./source-telemetry-view-models";

const props = defineProps<{
  sourceTelemetry: CodeReviewSourceTelemetryDto | null | undefined;
}>();

/**
 * All derived display state is precomputed here so both live review and test
 * result parents can pass the raw API DTO without duplicating template logic.
 */
const accordionItems = computed(() =>
  buildSourceTelemetryAccordionItems(props.sourceTelemetry),
);

const documentRows = computed(() =>
  buildSourceTelemetryDocumentRows(props.sourceTelemetry),
);

const snippetRows = computed(() =>
  buildSourceTelemetrySnippetRows(props.sourceTelemetry),
);

const toolRows = computed(() =>
  buildSourceTelemetryToolRows(props.sourceTelemetry),
);

const contentGapRows = computed(() =>
  buildSourceTelemetryContentGapRows(props.sourceTelemetry),
);
</script>
