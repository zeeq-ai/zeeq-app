<template>
  <section class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <div
      v-if="!pullRequest"
      class="hidden flex-1 items-center justify-center lg:flex"
    >
      <UIcon name="i-hugeicons-git-pull-request" class="size-32 text-dimmed" />
    </div>

    <template v-else>
      <!-- Selected PR summary drives manual requests and links back to GitHub. -->
      <div class="border-b border-default p-4 sm:px-6">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div class="flex min-w-0 flex-1 items-start gap-2">
            <UButton
              v-if="showClose"
              icon="i-hugeicons-cancel-01"
              aria-label="Close pull request details"
              color="neutral"
              variant="ghost"
              size="sm"
              square
              class="-ms-2 shrink-0"
              @click="emits('close')"
            />

            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="truncate text-xl font-semibold text-highlighted">
                  #{{ pullRequest.pullRequestNumber }} {{ pullRequest.title }}
                </h2>
                <UBadge
                  :label="pullRequest.state"
                  color="neutral"
                  variant="outline"
                  class="rounded-full"
                />
                <UBadge
                  v-if="pullRequest.isDraft"
                  label="Draft"
                  color="warning"
                  variant="subtle"
                  class="rounded-full"
                />
                <UBadge
                  v-if="pullRequest.checkRunBlocking"
                  label="Merge blocked"
                  color="warning"
                  variant="subtle"
                  class="rounded-full"
                />
              </div>

              <p class="mt-1 text-sm text-muted">
                {{ pullRequest.ownerQualifiedRepoName }} by
                {{ pullRequest.authorLogin }}
              </p>
              <p class="mt-1 truncate font-mono text-xs text-muted">
                {{ pullRequest.branch }} &rarr; {{ pullRequest.baseBranch }} -
                {{ pullRequest.headSha }}
              </p>
            </div>
          </div>

          <UFieldGroup class="ms-auto shrink-0 justify-end" size="sm">
            <UTooltip text="View on GitHub" :delayDuration="0">
              <UButton
                icon="i-hugeicons-github"
                color="neutral"
                variant="outline"
                :to="pullRequest.htmlUrl"
                target="_blank"
              />
            </UTooltip>
            <!-- Mode 1 share link — built entirely client-side from (id, createdAtUtc). -->
            <UTooltip text="Copy link to PR" :delayDuration="0">
              <UButton
                icon="i-hugeicons-link-01"
                aria-label="Copy pull request link"
                color="neutral"
                variant="outline"
                size="sm"
                square
                @click="copyPullRequestLink(pullRequest)"
              />
            </UTooltip>
            <UTooltip text="Bypass active PR check" :delayDuration="0">
              <UButton
                v-if="pullRequest.checkRunBlocking"
                icon="i-hugeicons-chat-unlock"
                color="neutral"
                variant="outline"
                size="sm"
                :loading="bypassing"
                @click="emits('bypassCheck', pullRequest)"
              />
            </UTooltip>
            <UButton
              label="Run review"
              icon="i-hugeicons-message-programming"
              color="neutral"
              variant="outline"
              :loading="requestingReviewId === pullRequest.id"
              @click="emits('requestReview', pullRequest)"
            />
          </UFieldGroup>
        </div>
      </div>

      <div v-if="loading" class="grid gap-3 p-4 sm:px-6">
        <USkeleton v-for="index in 3" :key="index" class="h-28 rounded-md" />
      </div>

      <div v-else class="min-h-0 flex-1 overflow-y-auto">
        <CodeReviewAccordion
          :reviews
          v-model:open-review-id="openReviewId"
          :review-findings-by-review-key
          :loading-review-findings-by-review-key
          :review-findings-errors-by-review-key
          :cart-content-hashes
          @load-review-findings="emits('loadReviewFindings', $event)"
          @toggle-cart="
            (finding, reviewer, review, annotation) =>
              emits('toggleCart', finding, reviewer, review, annotation)
          "
        />
      </div>
    </template>
  </section>
</template>

<script setup lang="ts">
import type {
  CodeReviewFindingDto,
  CodeReviewFindingsResponse,
  CodeReviewPullRequestDto,
  CodeReviewRecordDto,
  CodeReviewReviewerFindingsDto,
} from "@/api/generated";
import { toAbsoluteAppUrl } from "@/router/app-url";

import CodeReviewAccordion from "./CodeReviewAccordion.vue";

const toast = useToast();

defineProps<{
  pullRequest: CodeReviewPullRequestDto | null;
  reviews: CodeReviewRecordDto[];
  reviewFindingsByReviewKey: Record<string, CodeReviewFindingsResponse>;
  loadingReviewFindingsByReviewKey: Record<string, boolean>;
  reviewFindingsErrorsByReviewKey: Record<string, string>;
  loading: boolean;
  requestingReviewId: string | null;
  showClose?: boolean;
  cartContentHashes: Set<string>;
}>();

/** Relays the accordion open/close state between PullRequestReviews and CodeReviewAccordion. */
const openReviewId = defineModel<string>("openReviewId");

const bypassing = defineModel<boolean>("bypassing", { default: false });

const emits = defineEmits<{
  requestReview: [pullRequest: CodeReviewPullRequestDto];
  loadReviewFindings: [review: CodeReviewRecordDto];
  close: [];
  bypassCheck: [pullRequest: CodeReviewPullRequestDto];
  toggleCart: [
    finding: CodeReviewFindingDto,
    reviewer: CodeReviewReviewerFindingsDto,
    review: CodeReviewRecordDto,
    annotation: string | null,
  ];
}>();

/**
 * Builds and copies a Mode 1 standalone PR link entirely client-side.
 * The `c` token is minted server-side on every PR DTO (CodeReviewSingleViewToken.Encode),
 * encoding the partition timestamp as a 12-char Base64Url string with no percent-escaping.
 * NOTE: This replaced the earlier ?createdAtUtc=<iso> shape — JS Date truncates microseconds
 * so the raw token avoids the precision loss that caused 404s on the partition lookup.
 */
async function copyPullRequestLink(pr: CodeReviewPullRequestDto) {
  const url = toAbsoluteAppUrl(
    `/code-reviews/pull-requests/${pr.id}/single?c=${pr.singleViewToken}`,
  );

  try {
    await navigator.clipboard.writeText(url);
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
</script>
