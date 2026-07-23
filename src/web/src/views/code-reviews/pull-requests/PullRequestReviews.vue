<template>
  <div class="flex h-full min-h-0 flex-col overflow-hidden">
    <UAlert
      v-if="codeReviewError"
      title="Code reviews unavailable"
      :description="codeReviewError"
      icon="i-hugeicons-alert-02"
      color="error"
      variant="subtle"
      class="m-4 sm:m-6"
    />

    <div
      v-if="!loadingRepositories && !hasConfiguredRepositories"
      class="flex min-h-0 flex-1 items-center justify-center p-6"
    >
      <UEmpty
        icon="i-hugeicons-github"
        title="No repositories configured"
        description="Connect GitHub and enable repositories before requesting PR code reviews."
      >
        <template #actions>
          <UButton
            label="Open GitHub settings"
            icon="i-hugeicons-settings-01"
            color="neutral"
            to="/settings/github"
          />
        </template>
      </UEmpty>
    </div>

    <div v-else class="flex min-h-0 flex-1 overflow-hidden">
      <!-- Inbox: newest PR rows, filtered by selected repository when set. -->
      <PullRequestInboxList
        :pull-requests
        :latest-review-updates-by-pull-request-id
        :pull-request-ui-state-by-id
        :selected-pull-request-id="selectedPullRequest?.id ?? null"
        :loading="loadingPullRequests"
        :has-unread-updates="hasUnreadPullRequestUpdates"
        :has-next-page="pullRequestNextCursor !== null"
        :inbox-scope
        :inbox-scope-items
        :pull-request-number-filter="prNumberFilterInput"
        :can-filter-by-number="canFilterByNumber"
        :finding-pull-request="loadingSelectedPullRequest"
        @select="selectPullRequest"
        @refresh="refreshInbox"
        @change-scope="handleInboxScopeChange"
        @mark-read="markInboxRead"
        @load-more="loadNextPage"
        @filter-by-number="handlePrNumberFilter"
      />

      <!-- Detail: selected PR metadata and newest review rows. -->
      <PullRequestReviewDetail
        :pull-request="selectedPullRequest"
        :reviews="actualSelectedPullRequestReviews"
        :review-findings-by-review-key
        :loading-review-findings-by-review-key
        :review-findings-errors-by-review-key
        :loading="loadingSelectedPullRequest"
        :requesting-review-id
        v-model:open-review-id="openReviewId"
        v-model:bypassing="bypassing"
        :cart-content-hashes="cartContentHashes"
        class="hidden lg:flex"
        @request-review="requestReview"
        @bypass-check="handleBypassCheck"
        @load-review-findings="loadReviewFindings"
        @toggle-cart="handleToggleCart"
      />

      <USlideover
        v-if="isMobile"
        v-model:open="detailPanelOpen"
        :ui="{ content: 'max-w-xl' }"
      >
        <template #content>
          <PullRequestReviewDetail
            :pull-request="selectedPullRequest"
            :reviews="actualSelectedPullRequestReviews"
            :review-findings-by-review-key
            :loading-review-findings-by-review-key
            :review-findings-errors-by-review-key
            :loading="loadingSelectedPullRequest"
            :requesting-review-id
            v-model:open-review-id="openReviewId"
            v-model:bypassing="bypassing"
            :cart-content-hashes="cartContentHashes"
            show-close
            class="h-full"
            @request-review="requestReview"
            @bypass-check="handleBypassCheck"
            @load-review-findings="loadReviewFindings"
            @toggle-cart="handleToggleCart"
            @close="closeDetailPanel"
          />
        </template>
      </USlideover>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, toRef, watch } from "vue";
import { storeToRefs } from "pinia";
import { useRoute } from "vue-router";
import { breakpointsTailwind, useBreakpoints } from "@vueuse/core";
import {
  CodeReviews,
  codeReviewInboxScopeEnum,
  codeReviewStatusEnum,
  type CodeReviewInboxScope,
  type CodeReviewFindingsResponse,
  type CodeReviewPullRequestDto,
  type CodeReviewRecordDto,
} from "@/api/generated";
import { useCodeReviewStore } from "@/stores/code-review-store";
import { useCartStore } from "@/stores/cart-store";
import { useAppStore } from "@/stores/app-store";
import type {
  CodeReviewFindingDto,
  CodeReviewReviewerFindingsDto,
} from "@/api/generated";

import { useCodeReviewDeepLink } from "@/composables/useCodeReviewDeepLink";

import PullRequestInboxList from "./PullRequestInboxList.vue";
import PullRequestReviewDetail from "./PullRequestReviewDetail.vue";

/**
 * Route query params injected via router `props`.
 *
 * prId: pre-selects a loaded inbox PR by record id.
 * reviewId: expands the matching code review accordion item on the selected PR.
 * prNumber + repositoryId: Mode 2 deep-link — resolves a PR by repo-scoped provider number.
 *   Both are required together; prNumber alone is ambiguous across repositories.
 */
const props = defineProps<{
  prId?: string;
  reviewId?: string;
  prNumber?: string;
  repositoryId?: string;
}>();

const pollIntervalMs = 7_500;

const router = useRouter();
const route = useRoute();
const toast = useToast();
const codeReviewStore = useCodeReviewStore();
const cartStore = useCartStore();
const appStore = useAppStore();
const { cartContentHashes } = storeToRefs(cartStore);
const {
  pullRequests,
  inboxScope,
  pullRequestNextCursor,
  selectedPullRequest,
  selectedPullRequestReviews,
  reviewFindingsByReviewKey,
  loadingReviewFindingsByReviewKey,
  reviewFindingsErrorsByReviewKey,
  latestReviewUpdatesByPullRequestId,
  pullRequestUiStateById,
  loadingRepositories,
  loadingPullRequests,
  loadingSelectedPullRequest,
  requestingReviewId,
  selectedRepositoryId,
  error: codeReviewError,
  activeOrganizationId,
  hasConfiguredRepositories,
  hasUnreadPullRequestUpdates,
} = storeToRefs(codeReviewStore);

const inboxScopeItems = [
  { label: "Mine", value: codeReviewInboxScopeEnum.Mine },
  { label: "All", value: codeReviewInboxScopeEnum.All },
];

/** Controlled value for the PR number lookup input (Mode 2 deep-link). */
const prNumberFilterInput = ref("");

/** True when a repository is selected; required for repo-scoped number lookup. */
const canFilterByNumber = computed(() => selectedRepositoryId.value !== null);

let pollHandle: number | null = null;
let polling = false;

/** Excludes PR-status placeholders so empty history renders the review UEmpty. */
const actualSelectedPullRequestReviews = computed(() =>
  selectedPullRequestReviews.value.filter(isActualCodeReview),
);

/**
 * URL deep-link, back/forward, and accordion-expansion orchestration.
 * Ownd by this composable so the component stays focused on store wiring
 * and UI events.
 */
const { openReviewId, syncUrlAfterLoad, markInitialized, stripPrParams } =
  useCodeReviewDeepLink({
    prId: toRef(props, "prId"),
    reviewId: toRef(props, "reviewId"),
    pullRequests,
    selectedPullRequest,
    actualSelectedPullRequestReviews,
    selectPr: async (pr) => {
      await codeReviewStore.selectPullRequest(pr);
    },
    deselectPr,
    onError: (err) => showError("Could not load pull request", err),
  });

const breakpoints = useBreakpoints(breakpointsTailwind);
const isMobile = breakpoints.smaller("lg");
const detailPanelOpen = computed({
  get() {
    return isMobile.value && selectedPullRequest.value !== null;
  },
  set(value: boolean) {
    if (!value) {
      closeDetailPanel();
    }
  },
});

/** Guards against registering polling resources after an interrupted mount (route change while loading). */
let isUnmounted = false;

onMounted(async () => {
  await refreshInbox();

  if (props.prId) {
    const targetPr = pullRequests.value.find((pr) => pr.id === props.prId);
    if (targetPr) {
      try {
        await codeReviewStore.selectPullRequest(targetPr);
      } catch (err: unknown) {
        showError("Could not load pull request", err);
      }
    }
  }

  // Mode 2 deep-link: ?prNumber=123&repositoryId=<id>
  // Both params are required — a number alone is ambiguous across repositories.
  // NOTE: Call findAndSelectPullRequestByNumber directly with the route param rather
  // than routing through handlePrNumberFilter, which reads selectedRepositoryId from
  // the store and could race with the async setInboxRepositoryFilter update.
  if (props.prNumber && props.repositoryId) {
    try {
      await codeReviewStore.setInboxRepositoryFilter(props.repositoryId);
      prNumberFilterInput.value = props.prNumber;
      await codeReviewStore.findAndSelectPullRequestByNumber(
        props.repositoryId,
        Number(props.prNumber),
      );
    } catch (err: unknown) {
      showError("Could not load pull request", err);
    }
  }

  if (isUnmounted) return;

  syncUrlAfterLoad();
  markInitialized();

  startPolling();
  document.addEventListener("visibilitychange", handleVisibilityChange);
});

onBeforeUnmount(() => {
  isUnmounted = true;
  stopPolling();
  document.removeEventListener("visibilitychange", handleVisibilityChange);
});

/** Reload when the user switches organizations in the app shell. */
watch(activeOrganizationId, async () => {
  stopPolling();
  await refreshInbox();

  if (isUnmounted) return;

  startPolling();
});

/** Refreshes the repository list and first inbox page. */
async function refreshInbox() {
  try {
    await codeReviewStore.loadInbox();
  } catch (err: unknown) {
    showError("Could not load PR inbox", err);
  }
}

/** Applies the ownership scope and reloads so polling resumes from a matching cursor. */
async function handleInboxScopeChange(scope: CodeReviewInboxScope) {
  try {
    await codeReviewStore.setInboxScope(scope);
  } catch (err: unknown) {
    showError("Could not update inbox scope", err);
  }
}

/** Loads the next cursor page when the user reaches the end of the inbox. */
async function loadNextPage() {
  try {
    await codeReviewStore.loadPullRequests({ reset: false });
  } catch (err: unknown) {
    showError("Could not load more pull requests", err);
  }
}

/** Selects a PR, loads its detail/review history, and pushes its ID to the URL. */
async function selectPullRequest(pullRequest: CodeReviewPullRequestDto) {
  try {
    await codeReviewStore.selectPullRequest(pullRequest);
    router.push({ query: { prId: pullRequest.id } });
  } catch (err: unknown) {
    showError("Could not load pull request", err);
  }
}

/** Requests a manual review and shows the queue outcome. */
async function requestReview(pullRequest: CodeReviewPullRequestDto) {
  try {
    const response = await codeReviewStore.requestReview(pullRequest);
    toast.add({
      title: reviewRequestTitle(response.outcome),
      description: pullRequest.ownerQualifiedRepoName,
      icon: "i-hugeicons-message-programming",
      color: response.codeReview ? "success" : "neutral",
    });
  } catch (err: unknown) {
    showError("Could not request review", err);
  }
}

const bypassing = ref(false);

async function handleBypassCheck(pullRequest: CodeReviewPullRequestDto) {
  const orgId =
    appStore.currentOrganization?.id ?? appStore.user?.organizationId;
  if (!orgId) return;

  bypassing.value = true;
  try {
    const response = await CodeReviews.bypassCheckRun(
      orgId,
      pullRequest.repositoryId,
      pullRequest.pullRequestNumber,
    );
    if (response.cleared) {
      toast.add({
        title: "Check cleared",
        description: `Merge block removed for #${pullRequest.pullRequestNumber}.`,
        icon: "i-hugeicons-tick-02",
        color: "success",
      });
      await selectPullRequest(pullRequest);
    } else {
      toast.add({
        title: "No blocking check",
        icon: "i-hugeicons-information-circle",
        color: "neutral",
      });
    }
  } catch {
    toast.add({
      title: "Bypass failed",
      description: "Could not clear the check run. Try again.",
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  } finally {
    bypassing.value = false;
  }
}

/** Clears frontend-only new/update markers after the user has seen them. */
function markInboxRead() {
  codeReviewStore.markInboxRead();
}

/** Clears the selected PR and its loaded reviews so no stale detail remains. */
function deselectPr() {
  selectedPullRequest.value = null;
  selectedPullRequestReviews.value = [];
}

/** Closes the mobile detail slideover, clears selection, and strips PR params from the URL. */
function closeDetailPanel() {
  deselectPr();
  router.replace({ query: stripPrParams() });
}

/**
 * Resolves a PR by repo-scoped number, injects it into the inbox, and syncs the URL.
 * null = user cleared the input; removes prNumber + repositoryId from the query.
 */
async function handlePrNumberFilter(value: number | null) {
  if (value === null) {
    prNumberFilterInput.value = "";
    router.replace({ query: stripPrNumberParams() });
    return;
  }

  const repoId = selectedRepositoryId.value;
  if (!repoId) return; // canFilterByNumber guard; defensive

  try {
    await codeReviewStore.findAndSelectPullRequestByNumber(repoId, value);
    router.replace({
      query: { ...route.query, prNumber: String(value), repositoryId: repoId },
    });
  } catch (err: unknown) {
    showError(`Could not find PR #${value}`, err);
    prNumberFilterInput.value = "";
  }
}

/** Returns the current query without the Mode 2 deep-link params. */
function stripPrNumberParams(): Record<string, string | undefined> {
  const {
    prNumber: _prNumber,
    repositoryId: _repositoryId,
    ...rest
  } = route.query;
  return rest as Record<string, string | undefined>;
}

/** Loads one review's detailed findings when the accordion row opens. */
async function loadReviewFindings(
  review: CodeReviewRecordDto,
): Promise<CodeReviewFindingsResponse | undefined> {
  try {
    return await codeReviewStore.loadReviewFindings(review);
  } catch (err: unknown) {
    showError("Could not load review findings", err);

    return undefined;
  }
}

/** Starts quiet visibility-aware polling while this route is active. */
function startPolling(
  options: { pullImmediately: boolean } = { pullImmediately: false },
) {
  if (document.visibilityState !== "visible") {
    return;
  }

  if (pollHandle === null) {
    pollHandle = window.setInterval(() => {
      void runPollingCycle();
    }, pollIntervalMs);
  }

  if (options.pullImmediately) {
    void runPollingCycle();
  }
}

/** Clears the inbox update poller when leaving this route or hiding the tab. */
function stopPolling() {
  if (pollHandle === null) {
    return;
  }

  window.clearInterval(pollHandle);
  pollHandle = null;
}

/** Runs both cursor-backed pollers without overlapping requests. */
async function runPollingCycle() {
  if (polling || document.visibilityState !== "visible") {
    return;
  }

  polling = true;

  try {
    await codeReviewStore.pollPullRequestUpdates();
    await codeReviewStore.pollInboxUpdates();
  } catch {
    // Polling is intentionally quiet; manual refresh surfaces persistent API
    // failures without interrupting inbox triage.
  } finally {
    polling = false;
  }
}

function handleVisibilityChange() {
  if (document.visibilityState === "visible") {
    startPolling({ pullImmediately: true });
    return;
  }

  stopPolling();
}

function reviewRequestTitle(outcome: string): string {
  if (outcome === "Queued") {
    return "Review queued";
  }

  if (outcome === "ActiveReviewAlreadyRunning") {
    return "Review already running";
  }

  if (outcome === "BudgetExhausted") {
    return "Review budget exhausted";
  }

  return "Review gated";
}

function isActualCodeReview(review: CodeReviewRecordDto): boolean {
  if (review.status !== codeReviewStatusEnum.Completed) {
    return true;
  }

  if (review.findingsStorageUri || review.failureMessage) {
    return true;
  }

  return totalFindings(review) > 0;
}

function totalFindings(review: CodeReviewRecordDto): number {
  return (
    toNumber(review.criticalFindings) +
    toNumber(review.majorFindings) +
    toNumber(review.minorFindings) +
    toNumber(review.suggestionFindings) +
    toNumber(review.commentFindings)
  );
}

function toNumber(value: number | string): number {
  return typeof value === "number" ? value : Number(value) || 0;
}

/** Toggles a finding into/out of the active draft findings cart. */
async function handleToggleCart(
  finding: CodeReviewFindingDto,
  reviewer: CodeReviewReviewerFindingsDto,
  review: CodeReviewRecordDto,
  annotation: string | null,
) {
  try {
    const { added, cartName } = await cartStore.toggleFinding(
      {
        level: finding.level,
        file: finding.file,
        line: typeof finding.line === "number" ? finding.line : null,
        side: finding.side,
        summary: finding.summary,
        body: finding.body,
      },
      reviewer,
      {
        ownerQualifiedRepoName: review.ownerQualifiedRepoName,
        pullRequestNumber: +review.pullRequestNumber,
      },
      annotation,
    );
    toast.add({
      title: added
        ? `Added to cart ${cartName}`
        : `Removed from cart ${cartName}`,
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err) {
    toast.add({
      title: "Could not update cart",
      description: err instanceof Error ? err.message : undefined,
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}

function showError(title: string, err: unknown) {
  toast.add({
    title,
    description: err instanceof Error ? err.message : "Code reviews failed.",
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
