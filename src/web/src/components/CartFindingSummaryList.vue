<template>
  <!-- Shared compact finding list used by both mutable drafts and saved carts. -->
  <div class="mt-2 grid min-w-0 gap-1">
    <div v-for="item in items" :key="item.hash" :class="itemRowClass">
      <div class="min-w-0 flex-1 overflow-hidden">
        <p class="truncate text-sm font-medium text-highlighted">
          {{ item.title }}
        </p>
        <p class="truncate text-xs text-muted">
          {{ item.facet }} · {{ item.summary
          }}{{ item.annotation ? ` — "${item.annotation}"` : "" }}
        </p>
      </div>

      <div v-if="$slots.actions" class="shrink-0">
        <slot name="actions" :item="item" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
type CartFindingSummaryListItem = {
  hash: string;
  title: string;
  facet: string;
  summary: string;
  annotation?: string | null;
};

const props = withDefaults(
  defineProps<{
    items: readonly CartFindingSummaryListItem[];
    indented?: boolean;
    interactive?: boolean;
  }>(),
  {
    indented: false,
    interactive: false,
  },
);

/** Centralizes row spacing/hover treatment so draft and saved lists stay aligned. */
const itemRowClass = computed(() => [
  "flex min-w-0 items-center gap-2 rounded-md px-2 py-1.5",
  props.indented ? "pl-11" : "",
  props.interactive ? "hover:bg-elevated/40" : "",
]);
</script>
