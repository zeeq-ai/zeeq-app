<template>
  <section class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <div
      v-if="!reviewId || !token"
      class="flex-1 flex items-center justify-center"
    >
      <UEmpty
        icon="i-hugeicons-alert-02"
        title="Invalid review link"
        description="The review link is missing required parameters."
        class="py-16"
      />
    </div>

    <div v-else-if="loadingSingleReview" class="grid gap-3 p-4 sm:px-6">
      <USkeleton v-for="index in 3" :key="index" class="h-28 rounded-md" />
    </div>

    <UAlert
      v-else-if="error"
      icon="i-hugeicons-alert-02"
      color="error"
      variant="subtle"
      :title="error"
      class="m-4"
    />

    <template v-else-if="singleReview">
      <div
        class="mx-auto flex min-h-0 w-full flex-1 flex-col lg:border-x lg:border-default"
        style="max-width: 1024px"
      >
        <!-- Slim header: review title, status, repo metadata, no PR list or request button -->
        <div class="border-b border-default p-4 sm:px-6">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="truncate text-xl font-semibold text-highlighted">
                  {{ singleReview.title }}
                </h2>
                <UBadge
                  :label="singleReview.status"
                  color="neutral"
                  variant="outline"
                  class="rounded-full"
                />
                <UBadge
                  :label="singleReview.requestOrigin"
                  color="neutral"
                  variant="subtle"
                  class="rounded-full"
                />
              </div>
              <p class="mt-1 text-sm text-muted">
                {{ singleReview.ownerQualifiedRepoName }}
                <template v-if="Number(singleReview.pullRequestNumber) > 0">
                  #{{ singleReview.pullRequestNumber }}
                </template>
                by {{ singleReview.authorLogin }}
              </p>
              <p class="mt-1 font-mono text-xs text-muted">
                Reviewed
                {{ new Date(singleReview.createdAtUtc).toLocaleString() }}
              </p>
            </div>
            <UFieldGroup v-if="singleReviewPullRequest" class="shrink-0" size="sm">
              <UTooltip text="Bypass active PR check" :delayDuration="0">
                <UButton
                  v-if="singleReviewPullRequest.checkRunBlocking"
                  icon="i-hugeicons-chat-unlock"
                  color="neutral"
                  variant="outline"
                  :loading="bypassing"
                  @click="handleBypassCheck"
                />
              </UTooltip>
              <UTooltip text="Copy link to review" :delayDuration="0">
                <UButton
                  icon="i-hugeicons-link-01"
                  aria-label="Copy review link"
                  color="neutral"
                  variant="outline"
                  square
                  @click="copyReviewLink"
                />
              </UTooltip>
              <UTooltip text="View on GitHub" :delayDuration="0">
                <UButton
                  icon="i-hugeicons-github"
                  color="neutral"
                  variant="outline"
                  :to="singleReviewPullRequest.htmlUrl"
                  target="_blank"
                />
              </UTooltip>
            </UFieldGroup>
          </div>
        </div>

        <div
          v-if="singleReviewReviews?.length === 0"
          class="flex flex-1 items-center justify-center"
        >
          <UEmpty
            icon="i-hugeicons-message-programming"
            title="No reviews"
            description="No related reviews found for this review."
            class="py-16"
          />
        </div>

        <!-- Reused accordion: first item auto-expanded -->
        <div v-else class="min-h-0 flex-1 overflow-y-auto">
          <CodeReviewAccordion
            :reviews="singleReviewReviews"
            v-model:open-review-id="openReviewId"
            :review-findings-by-review-key
            :loading-review-findings-by-review-key
            :review-findings-errors-by-review-key
            :cart-content-hashes
            @load-review-findings="handleLoadReviewFindings"
            @toggle-cart="
              (finding, reviewer, review, annotation) =>
                handleToggleCart(finding, reviewer, review, annotation)
            "
          />
        </div>
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { storeToRefs } from "pinia";
import type {
  CodeReviewFindingDto,
  CodeReviewRecordDto,
  CodeReviewReviewerFindingsDto,
} from "@/api/generated";
import { CodeReviews } from "@/api/generated";
import { useCodeReviewStore } from "@/stores/code-review-store";
import { useAppStore } from "@/stores/app-store";
import { useCartStore } from "@/stores/cart-store";

import CodeReviewAccordion from "@/views/code-reviews/pull-requests/CodeReviewAccordion.vue";

const props = defineProps<{
  reviewId: string;
  token?: string;
}>();

const codeReviewStore = useCodeReviewStore();
const cartStore = useCartStore();
const appStore = useAppStore();

const {
  singleReview,
  singleReviewReviews,
  singleReviewPullRequest,
  loadingSingleReview,
  reviewFindingsByReviewKey,
  loadingReviewFindingsByReviewKey,
  reviewFindingsErrorsByReviewKey,
  error,
} = storeToRefs(codeReviewStore);

const { cartContentHashes } = storeToRefs(cartStore);

/** Auto-expand the primary review on load. */
const openReviewId = ref<string | undefined>();
const bypassing = ref(false);

onMounted(async () => {
  if (!props.token) return;

  const response = await codeReviewStore.loadSingleReview(
    props.reviewId,
    props.token,
  );

  openReviewId.value = response.review.id;
});

async function handleLoadReviewFindings(review: CodeReviewRecordDto) {
  await codeReviewStore.loadReviewFindings(review);
}

const toast = useToast();

async function copyReviewLink() {
  try {
    await navigator.clipboard.writeText(location.href);
    toast.add({
      title: "Link copied",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch {
    toast.add({
      title: "Could not copy link",
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}

async function handleBypassCheck() {
  const pr = singleReviewPullRequest.value;
  const orgId =
    appStore.currentOrganization?.id ?? appStore.user?.organizationId;
  if (!pr || !orgId) return;

  bypassing.value = true;
  try {
    const response = await CodeReviews.bypassCheckRun(
      orgId,
      pr.repositoryId,
      pr.pullRequestNumber,
    );
    if (response.cleared) {
      toast.add({
        title: "Check cleared",
        description: `Merge block removed for #${pr.pullRequestNumber}.`,
        icon: "i-hugeicons-tick-02",
        color: "success",
      });
      await codeReviewStore.loadSingleReview(props.reviewId, props.token!);
    } else {
      toast.add({
        title: "No blocking check",
        description: "No active check run was found for this PR.",
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
  } catch {
    toast.add({
      title: "Could not update cart",
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}
</script>
