import { computed, ref, watch, type Ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import type {
  CodeReviewPullRequestDto,
  CodeReviewRecordDto,
} from "@/api/generated";

/**
 * Encapsulates URL-driven state for the Code Review PR inbox: deep-link
 * selection, accordion expansion, and browser back/forward sync.
 *
 * PullRequestReviews.vue wires this composable to the code-review store;
 * the composable owns the route watchers and URL mutation helpers but
 * delegates actual data-fetching to the callbacks.
 */
export function useCodeReviewDeepLink(options: {
  prId: Ref<string | undefined>;
  reviewId: Ref<string | undefined>;
  pullRequests: Ref<CodeReviewPullRequestDto[]>;
  selectedPullRequest: Ref<CodeReviewPullRequestDto | null>;
  actualSelectedPullRequestReviews: Ref<CodeReviewRecordDto[]>;
  selectPr: (pr: CodeReviewPullRequestDto) => Promise<void>;
  deselectPr: () => void;
  /**
   * Surfaces a failed back/forward PR load to the host component so it can
   * report the error the same way the initial `onMounted` load does. Without
   * this the failure would be swallowed and leave the user with no feedback.
   */
  onError?: (err: unknown) => void;
}) {
  const {
    prId,
    reviewId,
    pullRequests,
    selectedPullRequest,
    actualSelectedPullRequestReviews,
    selectPr,
    deselectPr,
    onError,
  } = options;

  const route = useRoute();
  const router = useRouter();

  const initialized = ref(false);

  /** Two-way model bound to the accordion via v-model:open-review-id. */
  const openReviewId = ref<string>();

  /**
   * Only forwards the URL `reviewId` when it belongs to the currently
   * selected PR, preventing the accordion from trying to expand a
   * review from a different PR.
   */
  const forwardedReviewId = computed<string | undefined>(() => {
    if (!reviewId.value) return undefined;

    return actualSelectedPullRequestReviews.value.some(
      (r) => r.id === reviewId.value,
    )
      ? reviewId.value
      : undefined;
  });

  /** URL → accordion model (downward). */
  watch(forwardedReviewId, (id) => {
    openReviewId.value = id;
  });

  /** Accordion model → URL (upward). */
  watch(openReviewId, (id, oldId) => {
    if (id === oldId) return;
    if (id) {
      router.replace({ query: { ...route.query, reviewId: id } });
      return;
    }
    const { reviewId: _, ...rest } = route.query as Record<string, unknown>;
    router.replace({ query: rest as Record<string, string> });
  });

  /**
   * Back/forward navigation handler.
   *
   * Selects the PR matching the URL `prId`.  When `prId` is absent,
   * clears the selection so the detail panel does not show a stale PR.
   */
  watch(prId, (val) => {
    if (!initialized.value) return;
    if (val === selectedPullRequest.value?.id) return;
    if (!val) {
      deselectPr();
      return;
    }
    const targetPr = pullRequests.value.find((pr) => pr.id === val);
    if (targetPr) {
      selectPr(targetPr).catch((err) => onError?.(err));
    }
  });

  /**
   * Replaces the current URL to reflect the initial auto-selected PR
   * (or deep-linked PR).  Uses `replace` so the initial load does not
   * create an extra history entry.
   */
  function syncUrlAfterLoad() {
    const pr = selectedPullRequest.value;
    if (!pr) return;
    const query: Record<string, string> = { prId: pr.id };
    const rid = forwardedReviewId.value;
    if (rid) {
      query.reviewId = rid;
    }
    router.replace({ query: { ...route.query, ...query } });
  }

  /** Marks the initial load as complete so back/forward watchers activate. */
  function markInitialized() {
    initialized.value = true;
  }

  /**
   * Strips PR-related query params so the URL matches a de-selected
   * state — used by the mobile detail panel close handler.
   */
  function stripPrParams(): Record<string, string> {
    const {
      prId: _,
      reviewId: __,
      ...rest
    } = route.query as Record<string, unknown>;
    return rest as Record<string, string>;
  }

  return {
    openReviewId,
    syncUrlAfterLoad,
    markInitialized,
    stripPrParams,
  };
}
