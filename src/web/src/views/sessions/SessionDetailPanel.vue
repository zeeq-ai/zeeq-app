<!--
Sessions detail panel: right side of the split view. Main column is a
UTimeline over prompt events only (the events with a user message payload);
USidebar mirrors Libraries.vue's collapsible="icon" pattern for conversation
metadata plus token/cost stats ported from v1 AgentConversationDetailPanel.vue.
Reused for both the desktop column and the mobile USlideover in Sessions.vue.
-->
<template>
  <div class="flex min-w-0 flex-1">
    <div class="flex min-w-0 flex-1 flex-col">
      <div
        v-if="conversation"
        class="flex min-h-16 items-center justify-between gap-3 border-b border-default px-4 py-3 sm:px-6"
      >
        <div class="flex min-w-0 items-center gap-2">
          <UBadge
            :label="conversation.harness"
            color="neutral"
            variant="subtle"
            size="sm"
            class="shrink-0 rounded-full"
          />
        </div>

        <div class="flex shrink-0 items-center gap-2">
          <UTabs
            v-if="canAutoRefresh"
            v-model="autoRefreshModel"
            :items="autoRefreshItems"
            :content="false"
            color="neutral"
            variant="pill"
            size="xs"
            class="shrink-0"
            :ui="compactTabsUi"
          />
          <UButton
            icon="i-hugeicons-refresh"
            aria-label="Refresh conversation"
            color="neutral"
            variant="ghost"
            size="sm"
            square
            :loading="loading"
            @click="emits('refresh')"
          />
          <UButton
            icon="i-hugeicons-information-circle"
            aria-label="Toggle conversation stats"
            color="neutral"
            variant="ghost"
            size="lg"
            square
            @click="sidebarOpen = !sidebarOpen"
          />
          <UButton
            v-if="showClose"
            icon="i-hugeicons-cancel-01"
            aria-label="Close conversation"
            color="neutral"
            variant="ghost"
            size="sm"
            square
            @click="emits('close')"
          />
        </div>
      </div>

      <div class="min-h-0 flex-1 overflow-y-auto p-4 sm:p-6">
        <div v-if="loading" class="grid gap-3">
          <USkeleton v-for="index in 4" :key="index" class="h-16 rounded-md" />
        </div>

        <UEmpty
          v-else-if="!conversation"
          icon="i-hugeicons-chat-user-01"
          title="Select a conversation"
          variant="naked"
          description="Choose a conversation from the inbox to see its prompt timeline."
          class="h-full py-12"
        />

        <template v-else>
          <div class="mb-4 grid grid-cols-1 gap-2 sm:grid-cols-3">
            <div class="min-w-0 rounded-md border border-default bg-elevated/20 px-3 py-2">
              <p class="text-xs text-muted">Total cost</p>
              <p class="mt-1 font-mono text-sm text-highlighted">
                {{ formatUsd(totalCostUsd) }}
              </p>
            </div>
            <div class="min-w-0 rounded-md border border-default bg-elevated/20 px-3 py-2">
              <p class="text-xs text-muted">Total tokens</p>
              <p class="mt-1 font-mono text-sm text-highlighted">
                {{ formatTokenCount(totalTokens) }}
              </p>
            </div>
            <div class="min-w-0 rounded-md border border-default bg-elevated/20 px-3 py-2">
              <p class="text-xs text-muted">Session length</p>
              <p class="mt-1 font-mono text-sm text-highlighted">
                {{ sessionLengthLabel }}
              </p>
            </div>
          </div>

          <UEmpty
            v-if="timelineItems.length === 0"
            icon="i-hugeicons-message-programming"
            title="No prompts"
            variant="naked"
            description="This conversation has no user-message events."
            class="h-full py-12"
          />

          <UTimeline
            v-else
            :items="timelineItems"
            size="sm"
            :ui="{
              title: 'font-mono font-bold text-highlighted',
              description: 'whitespace-pre-wrap break-words',
            }"
          />
        </template>
      </div>
    </div>

    <USidebar
      v-if="conversation"
      v-model:open="sidebarOpen"
      side="right"
      collapsible="icon"
      :style="{
        '--sidebar-width': '19rem',
        '--sidebar-width-icon': '3.5rem',
      }"
      :ui="{
        root: 'relative h-full',
        gap: 'h-full',
        container: 'absolute inset-y-0 end-0 z-10 flex h-full',
        inner: 'bg-default divide-transparent',
        body: 'p-0 sm:p-0',
      }"
    >
      <template #default="{ state }">
        <div class="flex h-full flex-col gap-4 overflow-y-auto p-3">
          <template v-if="state === 'expanded'">
            <div class="flex flex-col gap-1.5">
              <SessionStatRow label="Harness" :value="harnessLabel" />
              <SessionStatRow label="Models" :value="modelsLabel" />
              <SessionStatRow label="Owner" :value="ownerLabel" />
              <SessionStatRow label="Repository" :value="conversation.repoRemoteUrl ?? '—'" />
              <SessionStatRow label="Branch" :value="conversation.headBranch ?? '—'" />
              <SessionStatRow
                label="Started"
                :value="formatFullDateTime(conversation.startedAtUtc)"
              />
              <SessionStatRow
                label="Last activity"
                :value="lastActivityLabel"
              />
            </div>

            <USeparator />

            <div class="flex flex-col gap-1.5">
              <h3 class="px-2 text-xs font-medium text-dimmed uppercase">
                Token usage
              </h3>
              <SessionStatRow
                label="Total tokens"
                :value="formatTokenCount(totalTokens)"
              />
              <SessionStatRow
                label="Input / Output"
                :value="`${formatTokenCount(inputTokens)} / ${formatTokenCount(outputTokens)}`"
              />
              <SessionStatRow label="Total cost" :value="formatUsd(totalCostUsd)" />

              <template v-if="tokenUsage">
                <SessionStatRow
                  label="Cost / completion"
                  :value="formatUsd(tokenUsage.averageCostPerEventUsd)"
                />
                <SessionStatRow
                  label="Cache hit rate"
                  :value="formatPercent(tokenUsage.cacheHitRate)"
                />
                <SessionStatRow
                  label="Peak context"
                  :value="formatTokenCount(tokenUsage.peakInputTokens)"
                />
                <SessionStatRow
                  label="Reasoning share"
                  :value="formatPercent(tokenUsage.reasoningShareOfOutput)"
                />
              </template>
            </div>

            <template v-if="tokenUsage">
              <USeparator />

              <div class="flex flex-col gap-1.5">
                <div class="flex items-center gap-1 px-2">
                  <h3 class="text-xs font-medium text-dimmed uppercase">
                    Cost breakdown
                  </h3>
                  <UTooltip
                    text="Estimated split of the total cost using per-model token rates — not an independent cost source."
                  >
                    <UIcon
                      name="i-hugeicons-information-circle"
                      class="size-3.5 text-dimmed"
                    />
                  </UTooltip>
                </div>
                <SessionStatRow
                  label="Fresh input"
                  :value="formatUsd(tokenUsage.freshInputCostUsd)"
                />
                <SessionStatRow
                  label="Cached input"
                  :value="formatUsd(tokenUsage.cachedInputCostUsd)"
                />
                <SessionStatRow label="Output" :value="formatUsd(tokenUsage.outputCostUsd)" />
                <SessionStatRow
                  label="Cache savings"
                  :value="formatUsd(tokenUsage.cacheSavingsUsd)"
                />
              </div>
            </template>
          </template>

          <template v-else>
            <UTooltip text="Total tokens">
              <div class="flex flex-col items-center gap-1 px-2 py-1.5 text-muted">
                <UIcon name="i-hugeicons-coins-01" class="size-5" />
              </div>
            </UTooltip>
            <UTooltip text="Total cost">
              <div class="flex flex-col items-center gap-1 px-2 py-1.5 text-muted">
                <UIcon name="i-hugeicons-dollar-circle" class="size-5" />
              </div>
            </UTooltip>
          </template>
        </div>
      </template>
    </USidebar>
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { useIntervalFn } from "@vueuse/core";
import type { TabsItem, TimelineItem } from "@nuxt/ui";
import type {
  AgentConversationDetailResponse,
  MemberResponse,
} from "@/api/generated";
import {
  formatDuration,
  formatFullDateTime,
  formatPercent,
  formatTokenCount,
  formatTurnTokens,
  formatUsd,
  isTaskNotificationPrompt,
  resolveOwnerLabel,
  toApiNumber,
} from "./session-display";
import SessionStatRow from "./SessionStatRow.vue";

const AUTO_REFRESH_ELIGIBLE_AGE_MS = 2 * 24 * 60 * 60 * 1000;
const AUTO_REFRESH_INTERVAL_MS = 30_000;

const props = defineProps<{
  detail: AgentConversationDetailResponse | null;
  members: MemberResponse[];
  loading: boolean;
  showClose?: boolean;
}>();

const emits = defineEmits<{
  close: [];
  refresh: [];
}>();

const sidebarOpen = ref(true);

const conversation = computed(() => props.detail?.summary ?? null);
const tokenUsage = computed(() => props.detail?.tokenUsage ?? null);

const compactTabsUi = {
  list: "h-7 w-auto p-0.5",
  trigger: "h-6 grow-0 px-2 py-0 text-xs",
};

const autoRefreshItems: TabsItem[] = [
  { label: "Off", value: "off" },
  { label: "Auto", value: "on" },
];
const autoRefreshEnabled = ref(false);
const autoRefreshModel = computed({
  get: (): string => (autoRefreshEnabled.value ? "on" : "off"),
  set: (value: string | number) => {
    autoRefreshEnabled.value = value === "on";
  },
});

/** Auto-refresh is only offered for conversations still likely to be active. */
const canAutoRefresh = computed(() => {
  if (!conversation.value) {
    return false;
  }

  const startedAtMs = new Date(conversation.value.startedAtUtc).getTime();

  return Date.now() - startedAtMs <= AUTO_REFRESH_ELIGIBLE_AGE_MS;
});

const { pause: pauseAutoRefresh, resume: resumeAutoRefresh } = useIntervalFn(
  () => emits("refresh"),
  AUTO_REFRESH_INTERVAL_MS,
  { immediate: false },
);

watch(
  [autoRefreshEnabled, canAutoRefresh],
  ([enabled, eligible]) => {
    if (enabled && eligible) {
      resumeAutoRefresh();
    } else {
      pauseAutoRefresh();
    }
  },
  { immediate: true },
);

/** Selecting a different conversation always starts with auto-refresh off. */
watch(
  () => conversation.value?.id,
  () => {
    autoRefreshEnabled.value = false;
  },
);

/**
 * Prefer live detail aggregates over the conversation row's rollup columns. During
 * backfill, list/detail summary rollup counters are null and detail's event-derived
 * tokenUsage remains authoritative for an opened conversation.
 */
const inputTokens = computed(() =>
  tokenUsage.value
    ? toApiNumber(tokenUsage.value.billedInputTokens)
    : toApiNumber(conversation.value?.totalInputTokens),
);
const outputTokens = computed(() =>
  tokenUsage.value
    ? toApiNumber(tokenUsage.value.billedOutputTokens)
    : toApiNumber(conversation.value?.totalOutputTokens),
);
const totalTokens = computed(() => inputTokens.value + outputTokens.value);
const totalCostUsd = computed(
  () => tokenUsage.value?.totalCostUsd ?? conversation.value?.totalCostUsd,
);

const harnessLabel = computed(() =>
  conversation.value?.harnessVariant
    ? `${conversation.value.harness} (${conversation.value.harnessVariant})`
    : (conversation.value?.harness ?? "—"),
);

const ownerLabel = computed(() =>
  conversation.value
    ? resolveOwnerLabel(
        props.members,
        conversation.value.ownerEmail,
        conversation.value.createdById,
      )
    : "—",
);

/** Distinct models seen across this conversation's completions, e.g. "gpt-5.4-mini, gpt-5.5". */
const modelsLabel = computed(() => {
  const models = props.detail?.models ?? [];

  return models.length > 0 ? models.join(", ") : "—";
});

/**
 * The last prompt in this session's timeline, rather than the conversation's own
 * `completedAtUtc` (which only reflects the last *accepted event* server-side and
 * doesn't distinguish "still active" from "just hasn't been marked completed yet").
 * Shared by the "Last activity" sidebar row and the "Session length" headline tile.
 */
const lastActivityAtUtc = computed(() => {
  const prompts = props.detail?.prompts;
  const lastPrompt = prompts?.[prompts.length - 1];

  return lastPrompt?.occurredAtUtc ?? conversation.value?.startedAtUtc ?? null;
});

const lastActivityLabel = computed(() =>
  lastActivityAtUtc.value ? formatFullDateTime(lastActivityAtUtc.value) : "—",
);

/** Elapsed time between the conversation's start and its last observed activity. */
const sessionLengthLabel = computed(() =>
  formatDuration(conversation.value?.startedAtUtc, lastActivityAtUtc.value),
);

/**
 * Timeline shows only prompt events — user-authored messages, not tool/completion
 * activity — and excludes Claude Code's synthetic `<task-notification>` pings, which
 * are background-agent lifecycle noise rather than something the user typed.
 */
const timelineItems = computed<TimelineItem[]>(() =>
  (props.detail?.prompts ?? [])
    .filter((prompt) => !isTaskNotificationPrompt(prompt.promptText))
    .map((prompt) => ({
      value: prompt.id,
      date: formatFullDateTime(prompt.occurredAtUtc),
      // Token info takes the bold headline slot — the full prompt (description) is
      // already the message, so a truncated repeat of it added no information.
      title: formatTurnTokens(prompt.inputTokens, prompt.outputTokens),
      description: prompt.promptText ?? "",
      icon: "i-hugeicons-chat-user-01",
    })),
);
</script>
