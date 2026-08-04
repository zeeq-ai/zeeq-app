<!--
Sessions inbox: left column of the Sessions split view, ported from
PullRequestInboxList.vue. Always scoped to the caller's own conversations (no
Mine/All choice — see IAgentConversationQueryStore's remarks), cursor-paginated
rows, and an alias-info popover pointing at Settings → Me for email-alias setup.
-->
<template>
  <section
    class="flex min-h-0 basis-full flex-col border-r border-default lg:max-w-md lg:basis-md"
  >
    <div class="border-b border-default">
      <div
        class="flex min-h-16 items-center justify-between gap-3 px-4 py-3 sm:px-6"
      >
        <h2 class="min-w-0 truncate text-base font-semibold text-highlighted">
          {{ inboxTitle }}
        </h2>

        <div class="flex shrink-0 items-center gap-1">
          <UPopover
            mode="hover"
            enable-touch
            :open-delay="aliasPopoverOpenDelay"
            :close-delay="150"
            :content="{ side: 'bottom', align: 'center', sideOffset: 8 }"
            :ui="{ content: 'w-96 max-w-[calc(100vw-2rem)]' }"
          >
            <UButton
              icon="i-hugeicons-user-id-verification"
              aria-label="Email alias matching"
              color="neutral"
              variant="ghost"
              size="sm"
              square
            />

            <template #content>
              <UAlert
                title="Email alias matching"
                description="Your inbox includes conversations tied to your sign-in and any conversations whose reported owner email matches an alias. Add an email alias if your agent harness reports a different email than your Zeeq sign-in identity."
                icon="i-hugeicons-user-id-verification"
                color="neutral"
                variant="soft"
                orientation="horizontal"
                :actions="aliasAlertActions"
                :ui="aliasAlertUi"
              />
            </template>
          </UPopover>

          <UButton
            icon="i-hugeicons-refresh"
            aria-label="Refresh conversations"
            color="neutral"
            variant="ghost"
            size="sm"
            square
            :loading="loading"
            @click="emits('refresh')"
          />
        </div>
      </div>
    </div>

    <div
      v-if="loading && conversations.length === 0"
      class="grid gap-2 p-4 sm:px-6"
    >
      <USkeleton v-for="index in 6" :key="index" class="h-20 rounded-md" />
    </div>

    <UEmpty
      v-else-if="conversations.length === 0"
      icon="i-hugeicons-chat-user-01"
      title="No conversations"
      variant="naked"
      description="Agent sessions will appear here once telemetry is ingested."
      class="flex-1 py-12"
    />

    <div v-else class="min-h-0 flex-1 overflow-y-auto divide-y divide-default">
      <button
        v-for="row in inboxRows"
        :key="row.conversation.id"
        type="button"
        class="grid w-full cursor-pointer gap-1.5 border-l-2 px-4 py-3 text-left text-sm transition-colors sm:px-6"
        :class="row.classes"
        @click="emits('select', row.conversation)"
      >
        <div class="flex min-w-0 items-center justify-between gap-3">
          <span class="min-w-0 truncate text-sm font-bold text-highlighted">
            {{ row.costLabel }}
          </span>
          <UBadge
            :label="row.conversation.harness"
            color="neutral"
            variant="subtle"
            size="sm"
            class="shrink-0 rounded-full"
          />
        </div>

        <div v-if="row.title" class="min-w-0">
          <p class="truncate text-sm leading-5 text-highlighted">
            {{ row.title }}
          </p>
        </div>

        <div class="flex min-w-0 items-center justify-between gap-3">
          <span v-if="row.isReady" class="truncate text-xs leading-4 text-muted">
            {{ formatTokenCount(row.totalTokens) }} tokens
          </span>
          <UBadge
            v-else
            label="Recomputing"
            color="neutral"
            variant="soft"
            size="sm"
            class="shrink-0 rounded-full"
          />
          <span class="shrink-0 text-xs leading-4 text-muted">
            {{ row.timeAgo }}
          </span>
        </div>
      </button>
    </div>

    <div
      v-if="hasNextPage"
      class="flex justify-center border-t border-default px-3 py-2"
    >
      <UButton
        label="Load more"
        icon="i-hugeicons-arrow-down-01"
        color="neutral"
        variant="ghost"
        :loading="loadingMore"
        @click="emits('loadMore')"
      />
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { storeToRefs } from "pinia";
import { formatTimeAgo } from "@vueuse/core";
import type { AgentConversationListItemDto, MemberResponse } from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { formatTokenCount, formatUsd, toApiNumber } from "./session-display";

const props = defineProps<{
  conversations: AgentConversationListItemDto[];
  members: MemberResponse[];
  selectedConversationId: string | null;
  loading: boolean;
  loadingMore: boolean;
  hasNextPage: boolean;
}>();

const emits = defineEmits<{
  select: [conversation: AgentConversationListItemDto];
  refresh: [];
  loadMore: [];
}>();

const appStore = useAppStore();
const { user: me } = storeToRefs(appStore);

const aliasAlertActions = [
  {
    label: "Set alias",
    icon: "i-hugeicons-arrow-right-02",
    color: "neutral" as const,
    variant: "ghost" as const,
    to: "/settings/me",
  },
];

const aliasAlertUi = {
  root: "rounded-md",
  title: "text-sm",
  description: "text-xs",
};

const hasEmailAlias = computed(
  () => me.value?.aliases?.some((alias) => alias.kind === "email") === true,
);

// Once the user has an alias, the popover would just be repetitive noise on every hover —
// int32-max effectively disables the hover-open delay rather than removing the popover
// entirely (it's still reachable, just no longer pops open unprompted).
const aliasPopoverOpenDelay = computed(() =>
  hasEmailAlias.value ? 2_147_483_647 : 300,
);

const inboxTitle = computed(() => {
  const name = me.value?.name?.trim() || me.value?.email?.trim();

  return name ? `${name}'s sessions` : "My sessions";
});

type SessionInboxRow = {
  conversation: AgentConversationListItemDto;
  classes: string[];
  costLabel: string;
  title: string | null;
  isReady: boolean;
  totalTokens: number;
  timeAgo: string;
};

const activeClasses = ["border-l-primary", "bg-primary/10"];
const defaultClasses = [
  "border-l-transparent",
  "hover:border-l-primary",
  "hover:bg-primary/5",
];

/**
 * Cached template-ready projection keeps row state and styling out of the markup.
 * totalTokens sums only ready conversation rollup columns. Recomputing rows remain
 * visible, but their server-projected counters are null until backfill catches up.
 * timeAgo uses VueUse's pure `formatTimeAgo` (not the reactive `useTimeAgo` composable) —
 * a one-shot relative-time string per row is all a paginated list needs, and calling a
 * composable inside `.map()` would spin up an unnecessary live-updating ref per row.
 *
 * TODO: the inbox row also doesn't render repoRemoteUrl/headBranch (available on the DTO
 * today); title, cost, and tokens are the first ready rollup projection.
 */
const inboxRows = computed<SessionInboxRow[]>(() =>
  props.conversations.map((conversation) => {
    const isReady = conversation.rollupStatus === "ready";

    return {
      conversation,
      classes:
        conversation.id === props.selectedConversationId
          ? activeClasses
          : defaultClasses,
      costLabel: isReady ? formatUsd(conversation.totalCostUsd) : "Recomputing",
      title: conversation.title?.trim() || null,
      isReady,
      totalTokens: isReady
        ? toApiNumber(conversation.totalInputTokens) +
          toApiNumber(conversation.totalOutputTokens)
        : 0,
      timeAgo: formatTimeAgo(new Date(conversation.startedAtUtc)),
    };
  }),
);
</script>
