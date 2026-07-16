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
        v-for="pullRequest in pullRequests"
        :key="pullRequest.id"
        type="button"
        class="grid w-full cursor-pointer gap-1.5 border-l-2 px-4 py-3 text-left text-sm transition-colors sm:px-6"
        :class="
          pullRequest.id === selectedPullRequestId
            ? 'border-l-primary bg-primary/10'
            : 'border-l-transparent hover:border-l-primary hover:bg-primary/5'
        "
        @click="emits('select', pullRequest)"
      >
        <div class="flex min-w-0 items-start justify-between gap-3">
          <div class="min-w-0 flex-1 overflow-hidden">
            <div class="flex items-center gap-2">
              <span
                class="block min-w-0 max-w-full truncate text-[13px] text-highlighted transition-opacity"
                :class="
                  hasUnreadState(pullRequest)
                    ? 'font-bold opacity-100'
                    : 'font-semibold opacity-60'
                "
              >
                #{{ pullRequest.pullRequestNumber }}
                {{ pullRequest.title }}
              </span>
              <UBadge
                v-if="pullRequest.isDraft"
                label="Draft"
                color="neutral"
                variant="subtle"
                size="sm"
                class="rounded-full"
              />
            </div>
            <p class="mt-0.5 truncate text-xs leading-4 text-muted">
              {{ pullRequest.ownerQualifiedRepoName }}
            </p>
          </div>

          <!--
          <UBadge
            :label="pullRequest.claimStatus"
            :color="
              pullRequest.claimStatus === 'Claimed' ? 'success' : 'neutral'
            "
            size="sm"
            variant="subtle"
            class="rounded-full"
          />
          -->
        </div>

        <div class="flex min-w-0 items-center justify-between gap-3">
          <p class="truncate text-xs leading-4 text-muted">
            {{ pullRequest.authorLogin }}
          </p>
          <span class="shrink-0 text-xs leading-4 text-muted">
            {{ formatDate(pullRequest.updatedAtUtc) }}
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
import { ref, watch } from "vue";
import type { CodeReviewPullRequestDto } from "@/api/generated";
import type { CodeReviewInboxUpdateDto } from "@/api/generated";
import type { PullRequestInboxUiState } from "@/stores/code-review-store";

const props = defineProps<{
  pullRequests: CodeReviewPullRequestDto[];
  latestReviewUpdatesByPullRequestId: Record<string, CodeReviewInboxUpdateDto>;
  pullRequestUiStateById: Record<string, PullRequestInboxUiState>;
  selectedPullRequestId: string | null;
  loading: boolean;
  hasUnreadUpdates: boolean;
  hasNextPage: boolean;
  /** Controlled value for the PR number lookup input (string for v-model). */
  pullRequestNumberFilter: string;
  /** True when a repository is selected; PR numbers are repo-scoped and require it. */
  canFilterByNumber: boolean;
  /** True while the number lookup is resolving (shows spinner). */
  findingPullRequest: boolean;
}>();

const emits = defineEmits<{
  select: [pullRequest: CodeReviewPullRequestDto];
  refresh: [];
  markRead: [];
  loadMore: [];
  /** null = cleared. Parent resolves via findAndSelectPullRequestByNumber. */
  filterByNumber: [value: number | null];
}>();

/** Local buffer so the input is free-typed; parent prop resets it on clear/error. */
const localNumberValue = ref(props.pullRequestNumberFilter ?? "");

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
</script>
