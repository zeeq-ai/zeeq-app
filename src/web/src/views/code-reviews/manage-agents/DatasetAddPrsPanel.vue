<template>
  <div
    class="grid min-h-0 items-start gap-5 lg:grid-cols-[minmax(0,1.35fr)_minmax(18rem,0.8fr)]"
  >
    <section class="order-2 grid min-h-0 content-start gap-3 lg:order-1">
      <div class="grid gap-1">
        <h3 class="font-semibold text-highlighted">Browse pull requests</h3>
        <p class="text-sm text-muted">
          Search all repository pull requests. Selections stay staged when you
          search or load another page.
        </p>
      </div>

      <div class="flex flex-wrap gap-2">
        <UInput
          v-model="searchText"
          icon="i-hugeicons-search-01"
          placeholder="Search pull requests..."
          class="min-w-48 flex-1"
          aria-label="Search repository pull requests"
          @update:model-value="searchChanged"
        />
        <UButton
          label="Search"
          icon="i-hugeicons-search-01"
          color="neutral"
          variant="outline"
          :loading="loading"
          :disabled="disabled"
          @click="requestTargets"
        />
      </div>

      <div
        class="h-[min(32rem,52dvh)] min-h-48 overflow-auto rounded-md border border-default"
      >
        <div
          v-if="loading && targets.length === 0"
          class="grid gap-3 p-3"
          aria-label="Loading pull requests"
        >
          <USkeleton v-for="index in 5" :key="index" class="h-12" />
        </div>
        <UEmpty
          v-else-if="targets.length === 0"
          icon="i-hugeicons-git-pull-request"
          title="No matching pull requests"
          description="Try another search or add pull request numbers or URLs."
          class="py-12"
        />
        <table v-else class="w-full text-left text-sm">
          <thead class="sticky top-0 bg-elevated text-xs text-muted">
            <tr>
              <th scope="col" class="w-10 px-3 py-2">
                <span class="sr-only">Select</span>
              </th>
              <th scope="col" class="px-2 py-2">PR</th>
              <th scope="col" class="px-2 py-2">Title</th>
              <th scope="col" class="px-2 py-2">Author</th>
              <th scope="col" class="px-2 py-2">Membership</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="target in targets"
              :key="target.pullRequestNumber"
              class="border-t border-default"
              :class="{
                'bg-primary/5': selectedForDisplay.has(
                  Number(target.pullRequestNumber),
                ),
              }"
            >
              <td class="px-3 py-2">
                <UCheckbox
                  :model-value="
                    selectedForDisplay.has(Number(target.pullRequestNumber))
                  "
                  :aria-label="`Select PR #${target.pullRequestNumber} ${target.title}`"
                  :disabled="
                    disabled ||
                    target.alreadyInDataset ||
                    capturedNumbers.has(Number(target.pullRequestNumber))
                  "
                  @update:model-value="toggleTarget(target)"
                />
              </td>
              <td class="whitespace-nowrap px-2 py-2 font-mono text-primary">
                #{{ target.pullRequestNumber }}
              </td>
              <td
                class="min-w-32 max-w-64 px-2 py-2 font-medium text-highlighted"
              >
                <UTooltip :text="target.title">
                  <span class="block truncate">{{ target.title }}</span>
                </UTooltip>
              </td>
              <td class="max-w-32 truncate px-2 py-2 text-muted">
                {{ target.authorLogin }}
              </td>
              <td class="whitespace-nowrap px-2 py-2 text-xs text-muted">
                {{
                  target.alreadyInDataset
                    ? "Already included"
                    : capturedNumbers.has(Number(target.pullRequestNumber))
                      ? "To add"
                      : "Not included"
                }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div class="flex flex-wrap items-center justify-between gap-2">
        <p class="text-sm text-muted" aria-live="polite">
          {{ selectedForDisplay.size }} selected
        </p>
        <div class="flex gap-2">
          <UButton
            v-if="hasMore"
            label="Load more"
            color="neutral"
            variant="ghost"
            :loading="loadingMore"
            :disabled="disabled || loading || loadingMore"
            @click="emits('loadMoreTargets')"
          />
          <UButton
            label="Resolve selected"
            icon="i-hugeicons-checkmark-circle-02"
            color="neutral"
            variant="outline"
            :loading="resolving"
            :disabled="disabled || resolving || selectedNumbers.size === 0"
            @click="resolveSelected"
          />
        </div>
      </div>
    </section>

    <section
      class="order-1 grid content-start gap-3 border-t border-default pt-5 lg:order-2 lg:border-l lg:border-t-0 lg:pl-5 lg:pt-0"
    >
      <div class="grid gap-1">
        <h3 class="font-semibold text-highlighted">Add by PR number or URL</h3>
        <p class="text-sm text-muted">
          Enter positive numbers, <code>#numbers</code>, comma or newline lists,
          or pull request URLs from this repository.
        </p>
      </div>

      <UTextarea
        v-model="manualEntries"
        :rows="4"
        placeholder="#252, #247, https://github.com/org/repo/pull/310"
        aria-label="Pull request numbers or URLs"
        :disabled="disabled || resolving"
      />
      <UButton
        label="Resolve PRs"
        icon="i-hugeicons-search-01"
        color="neutral"
        variant="outline"
        class="w-fit"
        :loading="resolving"
        :disabled="disabled || resolving || !manualEntries.trim()"
        @click="resolveManual"
      />

      <UAlert
        v-if="resolutionResults.length > 0"
        title="Resolution results"
        description="Valid selections remain staged when another entry cannot be resolved."
        color="neutral"
        variant="subtle"
      />
      <ul v-if="resolutionResults.length > 0" class="grid gap-2">
        <li
          v-for="(item, index) in resolutionResults"
          :key="`${item.entry}:${index}`"
          class="flex min-w-0 items-start gap-2 rounded-md border border-default p-2 text-sm"
        >
          <UIcon
            :name="
              item.errorCode
                ? 'i-hugeicons-alert-02'
                : 'i-hugeicons-checkmark-circle-02'
            "
            class="mt-0.5 size-4 shrink-0"
            :class="item.errorCode ? 'text-warning' : 'text-success'"
            aria-hidden="true"
          />
          <div class="min-w-0">
            <p class="font-medium text-highlighted">
              {{
                item.target
                  ? `#${item.target.pullRequestNumber} ${item.target.title}`
                  : item.preview
                    ? `#${item.preview.pullRequestNumber} ${item.preview.title}`
                    : item.entry
              }}
            </p>
            <p class="text-xs text-muted">
              {{
                item.errorMessage ??
                (item.target?.alreadyInDataset
                  ? "Already included"
                  : item.preview
                    ? "Snapshot captured"
                    : "Resolved")
              }}
            </p>
          </div>
        </li>
      </ul>

      <p class="text-xs text-muted">
        Browse and manual entries use the same selection list. Saving the
        dataset applies all valid new captures together.
      </p>
    </section>
  </div>
</template>

<script setup lang="ts">
import type {
  CodeReviewEvaluationPrResolutionDto,
  CodeReviewEvaluationPrTargetDto,
} from "@/api/generated";

const props = defineProps<{
  targets: CodeReviewEvaluationPrTargetDto[];
  stagedPullRequestNumbers: number[];
  resolutionResults: CodeReviewEvaluationPrResolutionDto[];
  loading: boolean;
  loadingMore: boolean;
  resolving: boolean;
  hasMore: boolean;
  disabled?: boolean;
}>();

const emits = defineEmits<{
  searchTargets: [query: string];
  loadMoreTargets: [];
  resolve: [entries: string[]];
}>();

const searchText = ref("");
const manualEntries = ref("");
const selectedNumbers = ref(new Set<number>());
const capturedNumbers = computed(() => new Set(props.stagedPullRequestNumbers));
const selectedForDisplay = computed(
  () =>
    new Set(
      [...selectedNumbers.value, ...capturedNumbers.value].filter(
        (number) =>
          !props.targets.some(
            (target) =>
              Number(target.pullRequestNumber) === number &&
              target.alreadyInDataset,
          ),
      ),
    ),
);

function searchChanged() {
  emits("searchTargets", searchText.value);
}

function requestTargets() {
  emits("searchTargets", searchText.value);
}

function toggleTarget(target: CodeReviewEvaluationPrTargetDto) {
  if (props.disabled || target.alreadyInDataset) {
    return;
  }

  const next = new Set(selectedNumbers.value);
  const pullRequestNumber = Number(target.pullRequestNumber);
  if (next.has(pullRequestNumber)) {
    next.delete(pullRequestNumber);
  } else {
    next.add(pullRequestNumber);
  }
  selectedNumbers.value = next;
}

function resolveSelected() {
  const entries = [...selectedNumbers.value].map(String);
  if (entries.length > 0) {
    emits("resolve", entries);
  }
}

function resolveManual() {
  const entries = manualEntries.value
    .split(/[\n,]+/)
    .map((entry) => entry.trim())
    .filter(Boolean);
  if (entries.length > 0) {
    emits("resolve", entries);
  }
}
</script>
