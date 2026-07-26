<template>
  <section
    class="flex min-h-0 basis-full flex-col border-r border-default lg:max-w-md lg:basis-md"
  >
    <div class="border-b border-default">
      <!-- Title row: Inbox heading, PR number lookup, and action buttons -->
      <div
        class="flex min-h-16 items-center justify-between gap-3 px-4 py-3 sm:px-6"
      >
        <div class="flex min-w-0 items-center gap-2">
          <UChip :show="hasUnreadUpdates" color="info" position="top-left">
            <h2 class="text-base font-semibold text-highlighted">Inbox</h2>
          </UChip>
        </div>

        <div class="flex shrink-0 items-center gap-1">
          <!-- Popover to direct the user set up an alias -->
          <UPopover
            mode="hover"
            :enable-touch="!hasGitHubAlias"
            :open-delay="aliasPopoverOpenDelay"
            :close-delay="150"
            :content="{ side: 'bottom', align: 'center', sideOffset: 8 }"
            :ui="{ content: 'w-96 max-w-[calc(100vw-2rem)]' }"
          >
            <UTabs
              v-model="inboxScopeModel"
              :items="inboxScopeItems"
              :content="false"
              color="neutral"
              variant="pill"
              size="xs"
              class="shrink-0"
              :ui="compactTabsUi"
            />

            <template #content>
              <UAlert
                title="Set an alias"
                description="'Mine' includes PRs claimed by you and PRs authored by your GitHub aliases. Add an alias if your GitHub login differs from your sign-in identity."
                icon="i-hugeicons-user-id-verification"
                color="neutral"
                variant="soft"
                orientation="horizontal"
                :actions="aliasAlertActions"
                :ui="aliasAlertUi"
              />
            </template>
          </UPopover>
          <!-- PR number lookup: resolves a repo-scoped number directly into the inbox. -->
          <UTooltip
            :text="
              canFilterByNumber
                ? undefined
                : 'Select a repository to search by PR number'
            "
          >
            <UInput
              :model-value="localNumberValue"
              placeholder="#PR"
              inputmode="numeric"
              maxlength="6"
              :disabled="!canFilterByNumber"
              :trailing-icon="
                findingPullRequest ? 'i-hugeicons-loading-03' : undefined
              "
              :ui="{ trailing: 'pe-1', root: 'w-20' }"
              size="sm"
              @update:model-value="handleNumberInput"
              @keydown="handleNumberKeydown"
            >
              <template
                v-if="!findingPullRequest && localNumberValue"
                #trailing
              >
                <UButton
                  color="neutral"
                  variant="link"
                  size="sm"
                  icon="i-hugeicons-cancel-01"
                  aria-label="Clear PR number filter"
                  @click="handleClear"
                />
              </template>
            </UInput>
          </UTooltip>
          <UTooltip text="Mark read">
            <UButton
              icon="i-hugeicons-inbox-check"
              aria-label="Mark read"
              color="neutral"
              variant="ghost"
              size="sm"
              square
              :disabled="!hasUnreadUpdates"
              @click="emits('markRead')"
            />
          </UTooltip>
          <UButton
            icon="i-hugeicons-refresh"
            aria-label="Refresh pull requests"
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
      v-if="loading && pullRequests.length === 0"
      class="grid gap-2 p-4 sm:px-6"
    >
      <USkeleton v-for="index in 6" :key="index" class="h-24 rounded-md" />
    </div>

    <UEmpty
      v-else-if="pullRequests.length === 0"
      icon="i-hugeicons-git-pull-request"
      title="No pull requests"
      variant="naked"
      description="New webhook activity will appear here after GitHub repositories are enabled."
      class="flex-1 py-12"
    />

    <div v-else class="min-h-0 flex-1 overflow-y-auto divide-y divide-default">
      <!-- PR rows stay button-based so selection is fast and accessible. -->
      <button
        v-for="row in inboxRows"
        :key="row.pullRequest.id"
        type="button"
        class="grid w-full cursor-pointer gap-1.5 border-l-2 px-4 py-3 text-left text-sm transition-colors sm:px-6"
        :class="row.classes"
        @click="emits('select', row.pullRequest)"
      >
        <div class="flex min-w-0 items-start justify-between gap-3">
          <div class="min-w-0 flex-1 overflow-hidden">
            <div class="flex items-center gap-2">
              <span
                class="block min-w-0 max-w-full truncate text-[13px] text-highlighted transition-opacity"
                :class="row.titleClasses"
              >
                #{{ row.pullRequest.pullRequestNumber }}
                {{ row.pullRequest.title }}
              </span>
            </div>
            <p class="mt-0.5 truncate text-xs leading-4 text-muted">
              {{ row.pullRequest.ownerQualifiedRepoName }}
            </p>
          </div>

          <div class="flex shrink-0 items-center gap-1">
            <UBadge
              v-if="row.pullRequest.isDraft"
              label="Draft"
              leading-icon="i-hugeicons-git-pull-request-draft"
              color="neutral"
              variant="subtle"
              size="sm"
              class="rounded-full"
            />
            <UBadge
              v-if="row.showLifecycleStatus"
              :label="row.pullRequest.state"
              :leading-icon="row.lifecycleIcon"
              color="neutral"
              variant="subtle"
              size="sm"
              class="rounded-full"
            />
          </div>
        </div>

        <div class="flex min-w-0 items-center justify-between gap-3">
          <p class="truncate text-xs leading-4 text-muted">
            {{ row.pullRequest.authorLogin }}
          </p>
          <span class="shrink-0 text-xs leading-4 text-muted">
            {{ formatDate(row.pullRequest.updatedAtUtc) }}
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
        :loading="loading"
        @click="emits('loadMore')"
      />
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import type { TabsItem } from "@nuxt/ui";
import {
  codeReviewInboxScopeEnum,
  pullRequestStateEnum,
  type CodeReviewInboxScope,
  type CodeReviewPullRequestDto,
} from "@/api/generated";
import type { CodeReviewInboxUpdateDto } from "@/api/generated";
import type { PullRequestInboxUiState } from "@/stores/code-review-store";
import { useAppStore } from "@/stores/app-store";

const props = defineProps<{
  pullRequests: CodeReviewPullRequestDto[];
  latestReviewUpdatesByPullRequestId: Record<string, CodeReviewInboxUpdateDto>;
  pullRequestUiStateById: Record<string, PullRequestInboxUiState>;
  selectedPullRequestId: string | null;
  loading: boolean;
  hasUnreadUpdates: boolean;
  hasNextPage: boolean;
  /** Ownership scope for the inbox rows and update cursor. */
  inboxScope: CodeReviewInboxScope;
  inboxScopeItems: TabsItem[];
  /** Controlled value for the PR number lookup input (string for v-model). */
  pullRequestNumberFilter: string;
  /** True when a repository is selected; PR numbers are repo-scoped and require it. */
  canFilterByNumber: boolean;
  /** True while the number lookup is resolving (shows spinner). */
  findingPullRequest: boolean;
}>();

const emits = defineEmits<{
  select: [pullRequest: CodeReviewPullRequestDto];
  changeScope: [scope: CodeReviewInboxScope];
  refresh: [];
  markRead: [];
  loadMore: [];
  /** null = cleared. Parent resolves via findAndSelectPullRequestByNumber. */
  filterByNumber: [value: number | null];
}>();

const appStore = useAppStore();
const { user: me } = storeToRefs(appStore);

/** Local buffer so the input is free-typed; parent prop resets it on clear/error. */
const localNumberValue = ref(props.pullRequestNumberFilter ?? "");

const compactTabsUi = {
  list: "h-7 w-auto p-0.5",
  trigger: "h-6 grow-0 px-2 py-0 text-xs",
};

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

const inboxScopeModel = computed({
  get: () => props.inboxScope,
  set: handleScopeUpdate,
});

const hasGitHubAlias = computed(
  () => me.value?.aliases?.some((alias) => alias.kind === "github") === true,
);

const aliasPopoverOpenDelay = computed(() =>
  hasGitHubAlias.value ? 2_147_483_647 : 300,
);

type PullRequestInboxRow = {
  pullRequest: CodeReviewPullRequestDto;
  classes: string[];
  titleClasses: string[];
  showLifecycleStatus: boolean;
  lifecycleIcon: string | undefined;
};

const inactiveTextClasses = [
  "!text-zinc-700",
  "[&_*]:!text-zinc-700",
  "[&_p]:opacity-40",
  "[&_span]:opacity-50",
  "dark:!text-zinc-300",
  "dark:[&_*]:!text-zinc-300",
];

const inactiveSelectedClasses = [
  "!border-l-zinc-400",
  "!bg-zinc-100",
  "dark:!border-l-zinc-600",
  "dark:!bg-zinc-800",
];

const inactiveClasses = ["!bg-zinc-50", "dark:!bg-zinc-850"];
const inactiveHoverClasses = [
  "hover:!border-l-zinc-300",
  "hover:!bg-zinc-100",
  "dark:hover:!border-l-zinc-700",
  "dark:hover:!bg-zinc-800",
];

const activeClasses = ["border-l-primary", "bg-primary/10"];
const defaultClasses = [
  "border-l-transparent",
  "hover:border-l-primary",
  "hover:bg-primary/5",
];

/** Cached template-ready projection keeps row state and styling out of the markup. */
const inboxRows = computed(() =>
  props.pullRequests.map((pullRequest) =>
    toInboxRow(pullRequest, props.selectedPullRequestId),
  ),
);

watch(
  () => props.pullRequestNumberFilter,
  (val) => {
    localNumberValue.value = val ?? "";
  },
);

/** Strips non-digits on each keystroke; lookup fires on Enter or Tab. */
function handleNumberInput(raw: string) {
  localNumberValue.value = raw.replace(/\D/g, "").slice(0, 6);
}

/** Triggers the lookup on Enter (stay focused) or Tab (let focus move naturally). */
function handleNumberKeydown(event: KeyboardEvent) {
  if (event.key !== "Enter" && event.key !== "Tab") return;
  const n = Number(localNumberValue.value);
  if (n > 0) {
    emits("filterByNumber", n);
  }
  if (event.key === "Enter") {
    event.preventDefault();
  }
}

/** Clears the filter when the user clicks the trailing × icon. */
function handleClear() {
  localNumberValue.value = "";
  emits("filterByNumber", null);
}

function handleScopeUpdate(value: string | number) {
  if (
    value === codeReviewInboxScopeEnum.Mine ||
    value === codeReviewInboxScopeEnum.All
  ) {
    emits("changeScope", value);
  }
}

/** Formats API timestamps compactly for dense inbox rows. */
function formatDate(value: Date): string {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit",
  }).format(new Date(value));
}

function inboxUiState(
  pullRequest: CodeReviewPullRequestDto,
): PullRequestInboxUiState | null {
  return props.pullRequestUiStateById[pullRequest.id] ?? null;
}

function hasUnreadState(pullRequest: CodeReviewPullRequestDto): boolean {
  return inboxUiState(pullRequest)?.unreadAtUtc !== null;
}

/** Projects API and local inbox state into the compact row shape rendered by the template. */
function toInboxRow(
  pullRequest: CodeReviewPullRequestDto,
  selectedPullRequestId: string | null,
): PullRequestInboxRow {
  const isInactive =
    pullRequest.state !== pullRequestStateEnum.Open && !pullRequest.isDraft;
  const isSelected = pullRequest.id === selectedPullRequestId;
  const hasUnread = hasUnreadState(pullRequest);
  const classes: string[] = [];

  if (isInactive) {
    classes.push(...inactiveTextClasses);

    if (isSelected) {
      classes.push(...inactiveSelectedClasses);
    } else {
      classes.push(...inactiveClasses, ...inactiveHoverClasses);
    }
  } else if (isSelected) {
    classes.push(...activeClasses);
  } else {
    classes.push(...defaultClasses);
  }

  return {
    pullRequest,
    classes,
    titleClasses: [
      hasUnread ? "font-bold opacity-100" : "font-semibold opacity-60",
      ...(isInactive ? ["italic"] : []),
    ],
    showLifecycleStatus: pullRequest.state !== pullRequestStateEnum.Open,
    lifecycleIcon: lifecycleIcon(pullRequest),
  };
}

/** Uses provider lifecycle icons to make terminal PR states scannable. */
function lifecycleIcon(pullRequest: CodeReviewPullRequestDto): string | undefined {
  switch (pullRequest.state) {
    case pullRequestStateEnum.Closed:
      return "i-hugeicons-git-pull-request-closed";
    case pullRequestStateEnum.Merged:
      return "i-hugeicons-git-merge";
    default:
      return undefined;
  }
}
</script>
