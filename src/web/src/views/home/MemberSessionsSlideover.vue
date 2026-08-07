<template>
  <!-- Display-only drill-down for one member's recent agent conversations. -->
  <USlideover
    v-model:open="open"
    :title="title"
    description="Recent agent conversations, newest first."
    side="right"
    :ui="{ content: 'max-w-2xl', body: 'p-0 sm:p-0' }"
    @after:leave="onAfterLeave"
  >
    <template #body>
      <div class="flex h-full min-h-96 flex-col">
        <div class="border-b border-default px-6 py-4">
          <div class="mb-3 flex items-center justify-between gap-4 text-sm">
            <span class="font-medium text-highlighted">Minimum cost</span>
            <span class="font-mono font-bold text-muted">
              {{ minimumCostLabel }}
            </span>
          </div>
          <USlider
            :model-value="minimumCostUsd"
            :min="0"
            :max="100"
            :step="5"
            color="neutral"
            size="sm"
            aria-label="Minimum session cost"
            @update:model-value="onMinimumCostUpdate"
          />
          <div class="mt-2 flex justify-between text-xs text-dimmed">
            <span>Default</span>
            <span>$100+</span>
          </div>
        </div>

        <div v-if="loading" class="grid gap-2 px-6 py-4">
          <USkeleton v-for="index in 6" :key="index" class="h-20 rounded-md" />
        </div>

        <UAlert
          v-else-if="error"
          title="Could not load conversations"
          :description="error"
          icon="i-hugeicons-alert-02"
          color="error"
          variant="subtle"
          class="m-6"
        />

        <UEmpty
          v-else-if="rows.length === 0"
          icon="i-hugeicons-chat-user-01"
          title="No conversations"
          :description="emptyDescription"
          class="px-6 py-16"
        />

        <UListbox
          v-else
          :model-value="undefined"
          value-key="value"
          :items="rows"
          :highlight-on-hover="false"
          class="min-h-0 flex-1"
          :ui="{
            root: 'ring-0 rounded-none',
            content: 'max-h-none',
            group: 'p-0',
            item: 'px-6 py-2 data-disabled:cursor-default data-disabled:opacity-100',
          }"
        >
          <template #item="{ item }">
            <div
              class="flex w-full min-w-0 items-start justify-between gap-3 text-left"
            >
              <div class="min-w-0 flex-1">
                <p class="truncate font-mono tabular-nums text-highlighted">
                  {{ item.tokenCountLabel }}
                </p>
                <p class="mt-1 truncate text-xs text-dimmed">
                  {{ item.startedAtLabel }}
                </p>
              </div>

              <div class="flex shrink-0 items-center gap-2">
                <UBadge
                  :label="item.harness"
                  color="neutral"
                  variant="subtle"
                  size="sm"
                />
                <UBadge
                  :label="item.costLabel"
                  color="neutral"
                  variant="subtle"
                  size="sm"
                  class="font-mono font-bold tabular-nums"
                />
              </div>
            </div>
          </template>
        </UListbox>
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import type { ListboxItem } from "@nuxt/ui";
import type { AgentConversationListItemDto } from "@/api/generated";
import {
  formatFullDateTime,
  formatTokenCount,
  formatUsd,
  toApiNumber,
} from "@/views/sessions/session-display";

type MemberSessionListItem = ListboxItem & {
  value: string;
  tokenCountLabel: string;
  startedAtLabel: string;
  harness: string;
  costLabel: string;
  disabled: true;
};

const open = defineModel<boolean>("open", { required: true });

const props = defineProps<{
  memberName: string | null;
  conversations: AgentConversationListItemDto[];
  loading: boolean;
  error: string | null;
}>();

const emits = defineEmits<{
  afterLeave: [];
  minimumCostChange: [minimumCostUsd: number];
}>();

const minimumCostUsd = ref(0);

const title = computed(() =>
  props.memberName ? `${props.memberName}'s sessions` : "Member sessions",
);
const minimumCostLabel = computed(() =>
  minimumCostUsd.value === 0
    ? "Default ($0.10+)"
    : `${formatUsd(minimumCostUsd.value)}+`,
);
const emptyDescription = computed(() =>
  minimumCostUsd.value === 0
    ? "No conversations with a known cost of at least $0.10 were found for this member."
    : `No conversations at or above ${formatUsd(minimumCostUsd.value)} were found for this member.`,
);

/** Projects API records into the complete display shape consumed by the item slot. */
const rows = computed<MemberSessionListItem[]>(() =>
  props.conversations.map((conversation) => {
    const tokenCountLabel = `${formatTokenCount(
      toApiNumber(conversation.totalInputTokens) +
        toApiNumber(conversation.totalOutputTokens),
    )} tokens`;

    return {
      value: conversation.id,
      label: tokenCountLabel,
      tokenCountLabel,
      startedAtLabel: formatFullDateTime(conversation.startedAtUtc),
      harness: conversation.harness,
      costLabel: formatUsd(conversation.totalCostUsd),
      disabled: true,
    };
  }),
);

/** Debounces pointer and keyboard changes so dragging cannot flood the list endpoint. */
const emitMinimumCostChange = useDebounceFn((value: number) => {
  if (open.value && value === minimumCostUsd.value) {
    emits("minimumCostChange", value);
  }
}, 300);

function onMinimumCostUpdate(value: number | number[] | undefined) {
  const nextValue = Array.isArray(value) ? value[0] : value;
  if (typeof nextValue === "number") {
    minimumCostUsd.value = nextValue;
    emitMinimumCostChange(nextValue);
  }
}

/** Each member drill-down starts from the server's default known-cost filter. */
function onAfterLeave() {
  minimumCostUsd.value = 0;
  emits("afterLeave");
}
</script>
