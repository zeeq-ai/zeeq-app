<template>
  <section class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <!-- Guard: both params are required to locate the partitioned PR record. -->
    <div
      v-if="!pullRequestRecordId || !c"
      class="flex-1 flex items-center justify-center"
    >
      <UEmpty
        icon="i-hugeicons-alert-02"
        title="Invalid pull request link"
        description="The pull request link is missing required parameters."
        class="py-16"
      />
    </div>

    <div v-else-if="loadingSinglePullRequest" class="grid gap-3 p-4 sm:px-6">
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

    <template v-else-if="singlePullRequest">
      <div
        class="mx-auto flex min-h-0 w-full flex-1 flex-col lg:border-x lg:border-default"
        style="max-width: 1024px"
      >
        <!-- Slim header: PR title, number, repo, author, state badges, GitHub link. -->
        <div class="border-b border-default p-4 sm:px-6">
          <div class="flex flex-wrap items-start justify-between gap-3">
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="truncate text-xl font-semibold text-highlighted">
                  {{ singlePullRequest.title }}
                </h2>
                <UBadge
                  v-if="singlePullRequest.isDraft"
                  label="Draft"
                  color="neutral"
                  variant="subtle"
                  class="rounded-full"
                />
                <UBadge
                  :label="singlePullRequest.state"
                  color="neutral"
                  variant="outline"
                  class="rounded-full"
                />
                <UBadge
                  v-if="singlePullRequest.checkRunBlocking"
                  label="Merge blocked"
                  color="warning"
                  variant="subtle"
                  class="rounded-full"
                />
              </div>
              <p class="mt-1 text-sm text-muted">
                {{ singlePullRequest.ownerQualifiedRepoName }}
                #{{ singlePullRequest.pullRequestNumber }} by
                {{ singlePullRequest.authorLogin }}
              </p>
            </div>
            <UFieldGroup class="shrink-0" size="sm">
              <UTooltip text="Bypass active PR check" :delayDuration="0">
                <UButton
                  v-if="singlePullRequest.checkRunBlocking"
                  icon="i-hugeicons-chat-unlock"
                  color="neutral"
                  variant="outline"
                  :loading="bypassing"
                  @click="handleBypassCheck"
                />
              </UTooltip>
              <UTooltip text="Copy link to PR" :delayDuration="0">
                <UButton
                  icon="i-hugeicons-link-01"
                  aria-label="Copy pull request link"
                  color="neutral"
                  variant="outline"
                  square
                  @click="copyPullRequestLink(singlePullRequest)"
                />
              </UTooltip>
              <UTooltip text="Add to dataset" :delayDuration="0">
                <UButton
                  v-if="canAddToDataset && singlePullRequest.repositoryId"
                  icon="i-hugeicons-database-02"
                  :aria-label="`Add PR #${singlePullRequest.pullRequestNumber} to dataset`"
                  color="neutral"
                  variant="outline"
                  size="sm"
                  square
                  @click="datasetPickerOpen = true"
                />
              </UTooltip>
              <UTooltip text="View on GitHub" :delayDuration="0">
                <UButton
                  icon="i-hugeicons-github"
                  color="neutral"
                  variant="outline"
                  :to="singlePullRequest.htmlUrl"
                  target="_blank"
                />
              </UTooltip>
            </UFieldGroup>
          </div>
        </div>

        <div
          v-if="singlePullRequestReviews?.length === 0"
          class="flex flex-1 items-center justify-center"
        >
          <UEmpty
            icon="i-hugeicons-message-programming"
            title="No reviews"
            description="No reviews found for this pull request."
            class="py-16"
          />
        </div>

        <!-- Reused accordion: findings lazy-load via shared store action. -->
        <div v-else class="min-h-0 flex-1 overflow-y-auto">
          <CodeReviewAccordion
            :reviews="singlePullRequestReviews"
            v-model:open-review-id="openReviewId"
            :review-findings-by-review-key
            :loading-review-findings-by-review-key
            :review-findings-errors-by-review-key
            :review-raw-xml-by-review-key
            :loading-review-raw-xml-by-review-key
            :review-raw-xml-errors-by-review-key
            :cart-content-hashes
            @load-review-findings="handleLoadReviewFindings"
            @load-raw-xml="handleLoadRawXml"
            @toggle-cart="
              (finding, reviewer, review, annotation) =>
                handleToggleCart(finding, reviewer, review, annotation)
            "
          />
        </div>
      </div>
    </template>
  </section>
  <AddPullRequestToDataset
    v-if="singlePullRequest && singlePullRequest.repositoryId"
    v-model:open="datasetPickerOpen"
    :pull-request="singlePullRequest"
    :repository-id="singlePullRequest.repositoryId"
    :max-cases="maxEvaluationCases"
    :can-open-editor="canOpenEditorForSinglePullRequest"
    @added="handleDatasetAdded"
    @open-editor="openEvaluationDatasetEditor"
  />
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { CodeReviews } from "@/api/generated";
import type {
  CodeReviewFindingDto,
  CodeReviewPullRequestDto,
  CodeReviewRecordDto,
  CodeReviewReviewerFindingsDto,
} from "@/api/generated";
import { useCodeReviewStore } from "@/stores/code-review-store";
import { useAppStore } from "@/stores/app-store";
import { useCartStore } from "@/stores/cart-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";
import { useCodeReviewEvaluationStore } from "@/stores/code-review-evaluation-store";
import { toAbsoluteAppUrl } from "@/router/app-url";

import CodeReviewAccordion from "@/views/code-reviews/pull-requests/CodeReviewAccordion.vue";
import AddPullRequestToDataset from "@/views/code-reviews/pull-requests/AddPullRequestToDataset.vue";

const props = defineProps<{
  pullRequestRecordId: string;
  c?: string;
}>();

const codeReviewStore = useCodeReviewStore();
const evaluationStore = useCodeReviewEvaluationStore();
const organizationSettingsStore = useOrganizationSettingsStore();
const cartStore = useCartStore();
const appStore = useAppStore();
const router = useRouter();
const { canManageOrganization } = storeToRefs(organizationSettingsStore);
const { availability: evaluationAvailability } = storeToRefs(evaluationStore);
const {
  agents,
  selectedManagementItemId,
  selectedRepositoryId,
  activeOrganizationId,
} = storeToRefs(codeReviewStore);

const {
  singlePullRequest,
  singlePullRequestReviews,
  loadingSinglePullRequest,
  reviewFindingsByReviewKey,
  loadingReviewFindingsByReviewKey,
  reviewFindingsErrorsByReviewKey,
  reviewRawXmlByReviewKey,
  loadingReviewRawXmlByReviewKey,
  reviewRawXmlErrorsByReviewKey,
  error,
} = storeToRefs(codeReviewStore);

const { cartContentHashes } = storeToRefs(cartStore);

/** Auto-expand the first review on load. */
const openReviewId = ref<string | undefined>();
const bypassing = ref(false);
const datasetPickerOpen = ref(false);
const canAddToDataset = computed(
  () => canManageOrganization.value && evaluationAvailability.value?.enabled,
);
const maxEvaluationCases = computed(() =>
  Number(evaluationAvailability.value?.maxCases ?? 25),
);
const canOpenEditorForSinglePullRequest = computed(
  () =>
    !!singlePullRequest.value &&
    selectedRepositoryId.value === singlePullRequest.value.repositoryId &&
    agents.value.length > 0,
);

const toast = useToast();

/** Guards against registering polling resources after an interrupted mount (route change while loading). */
let isUnmounted = false;

onMounted(async () => {
  void evaluationStore.loadAvailability();
  if (!props.c) return;

  await codeReviewStore.loadSinglePullRequest(
    props.pullRequestRecordId,
    props.c,
  );

  if (isUnmounted) return;

  openReviewId.value = singlePullRequestReviews.value[0]?.id;

  startPolling();
  document.addEventListener("visibilitychange", handleVisibilityChange);
});

watch(activeOrganizationId, () => {
  evaluationStore.clearAvailability();
  void evaluationStore.loadAvailability();
});

onBeforeUnmount(() => {
  isUnmounted = true;
  stopPolling();
  document.removeEventListener("visibilitychange", handleVisibilityChange);
});

// ── Polling: pick up new code reviews triggered by later commits ─────────

const pollIntervalMs = 7_500;
let pollHandle: number | null = null;

/** Starts quiet visibility-aware polling while this view is mounted. */
function startPolling() {
  if (!props.c || document.visibilityState !== "visible") {
    return;
  }

  if (pollHandle === null) {
    pollHandle = window.setInterval(() => {
      void runPollCycle();
    }, pollIntervalMs);
  }
}

/** Clears the poller when leaving this view or hiding the tab. */
function stopPolling() {
  if (pollHandle === null) {
    return;
  }

  window.clearInterval(pollHandle);
  pollHandle = null;
}

/**
 * NOTE: `pollSinglePullRequestReviews` is called fire-and-forget from the
 * interval and the visibility handler below; `void` alone silences the
 * unused-promise lint but does nothing about rejections. Matches the same
 * quiet-polling convention as PullRequestReviews.vue's runPollingCycle —
 * transient API failures are swallowed here since manual refresh (reload)
 * surfaces persistent failures without interrupting review reading.
 */
async function runPollCycle() {
  try {
    await codeReviewStore.pollSinglePullRequestReviews();
  } catch {
    // Intentionally quiet — see NOTE above.
  }
}

function handleVisibilityChange() {
  if (document.visibilityState === "visible") {
    startPolling();
    void runPollCycle();
    return;
  }

  stopPolling();
}

async function handleLoadReviewFindings(review: CodeReviewRecordDto) {
  await codeReviewStore.loadReviewFindings(review);
}

async function handleLoadRawXml(review: CodeReviewRecordDto) {
  await codeReviewStore.loadReviewRawXml(review);
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

async function handleBypassCheck() {
  const pr = singlePullRequest.value;
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
      await codeReviewStore.loadSinglePullRequest(
        props.pullRequestRecordId,
        props.c!,
      );
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

async function openEvaluationDatasetEditor(
  datasetId: string,
  pullRequestNumber: number,
) {
  const pullRequest = singlePullRequest.value;
  if (!pullRequest?.repositoryId || !canManageOrganization.value) return;
  await openEvaluationDatasetEditorFor(
    pullRequest.repositoryId,
    datasetId,
    pullRequestNumber,
  );
}

function handleDatasetAdded(result: {
  added: boolean;
  datasetId: string;
  repositoryId: string;
  datasetName: string;
  datasetVersion: number;
  pullRequestNumber: number;
  baseSha: string;
  headSha: string;
  capturedAtUtc: Date;
}) {
  const actionAvailable =
    selectedRepositoryId.value === result.repositoryId &&
    agents.value.length > 0;
  toast.add({
    title: result.added
      ? `Added #${result.pullRequestNumber} @ ${result.headSha.slice(0, 7)} to ${result.datasetName} v${result.datasetVersion}`
      : `#${result.pullRequestNumber} is already in ${result.datasetName} v${result.datasetVersion}`,
    color: result.added ? "success" : "info",
    icon: result.added
      ? "i-hugeicons-tick-02"
      : "i-hugeicons-information-circle",
    actions: actionAvailable
      ? [
          {
            label: result.added ? "Open in editor" : "Open existing",
            onClick: () =>
              void openEvaluationDatasetEditorFor(
                result.repositoryId,
                result.datasetId,
                result.pullRequestNumber,
              ),
          },
        ]
      : [
          {
            label: "Manage agents",
            to: "/code-reviews/manage-agents",
          },
        ],
  });
}

async function openEvaluationDatasetEditorFor(
  repositoryId: string,
  datasetId: string,
  pullRequestNumber: number,
) {
  try {
    if (selectedRepositoryId.value !== repositoryId) {
      await codeReviewStore.setSelectedRepository(repositoryId);
    }
    const reviewer =
      agents.value.find((item) => item.id === selectedManagementItemId.value) ??
      agents.value[0];
    if (!reviewer) {
      toast.add({
        title: "Dataset saved",
        description:
          "Configure a reviewer agent for this repository before opening its dataset editor.",
        color: "info",
        icon: "i-hugeicons-information-circle",
        actions: [
          {
            label: "Manage agents",
            to: "/code-reviews/manage-agents",
          },
        ],
      });
      return;
    }
    await router.push({
      name: "ManageAgent",
      params: { orgId: activeOrganizationId.value, agentId: reviewer.id },
      query: {
        repositoryId,
        datasetId,
        prNumber: String(pullRequestNumber),
      },
    });
  } catch (err: unknown) {
    toast.add({
      title: "Could not open the dataset editor",
      description: err instanceof Error ? err.message : undefined,
      icon: "i-hugeicons-alert-02",
      color: "error",
    });
  }
}
</script>
