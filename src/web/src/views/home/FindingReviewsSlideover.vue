<template>
  <!--
  Drill-down for the Critical/Major findings stat cards: a single panel that
  switches between severities via UTabs (rather than two separate slideover
  instances) and carries its own period selector — defaulted from the parent
  dashboard's window but overridable locally without back-propagating, so
  browsing a longer range in here never resets the rest of the dashboard.
  Lists review groups (deduplicated to the latest attempt per PR/agent
  session — see the store action's comment) with at least one finding of the
  active severity in the selected window. Each row opens the standalone
  review (or, for PR-backed groups, the full PR review history) in a new tab
  so the drill-down list stays open for browsing multiple rows.
  -->
  <USlideover
    v-model:open="open"
    :title="title"
    :description="description"
    side="right"
    :ui="{ content: 'max-w-2xl' }"
  >
    <template #body>
      <div class="flex h-full min-h-96 flex-col gap-3">
        <div class="flex flex-col gap-2">
          <div class="flex flex-wrap items-center justify-end gap-2">
            <USelectMenu
              v-model="selectedAuthors"
              :items="authorItems"
              multiple
              icon="i-hugeicons-user"
              placeholder="All users"
              class="w-52"
            />
            <USelectMenu
              v-model="selectedRepos"
              :items="repoItems"
              multiple
              icon="i-hugeicons-git-branch"
              placeholder="All repos"
              class="w-52"
            />
            <USelect
              v-model="activeWindow"
              :items="metricWindowItems"
              icon="i-hugeicons-clock-01"
              class="w-44"
            />
          </div>
          <UTabs
            v-model="activeSeverity"
            :items="severityTabItems"
            :content="false"
            variant="link"
            class="w-full"
          />
        </div>

        <template v-if="loading">
          <USkeleton v-for="index in 4" :key="index" class="h-14 rounded-md" />
        </template>

        <UListbox
          v-else-if="rows.length > 0"
          :items="rows"
          filter
          filter-placeholder="Filter by title, repo, or author..."
          class="w-full flex-1"
          :ui="{ root: 'flex-1', content: 'flex-1 max-h-none' }"
        >
          <template #item-leading>
            <UIcon
              name="i-hugeicons-alert-02"
              class="size-4"
              :class="severityColor === 'error' ? 'text-error' : 'text-warning'"
            />
          </template>
          <template #item-trailing="{ item }">
            <UBadge :color="severityColor" variant="subtle" size="sm">
              {{ item.count }} {{ severityLabel }}
            </UBadge>
          </template>
        </UListbox>

        <UEmpty
          v-else
          icon="i-hugeicons-alert-02"
          title="No findings"
          :description="`No ${severityLabel} findings in this window.`"
          class="py-16"
        />

        <UButton
          v-if="hasMore"
          label="Load more"
          color="neutral"
          variant="subtle"
          block
          :loading="loadingMore"
          @click="emits('loadMore', activeSeverity, activeWindow)"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { useTimeAgo } from "@vueuse/core";
import {
  codeReviewRequestOriginEnum,
  findingSeverityEnum,
  type FindingReviewListItemResponse,
  type FindingSeverity,
} from "@/api/generated";
import {
  metricWindowItems,
  toMetricNumber,
  type MetricWindowToken,
} from "@/stores/metrics-store";

/** One rendered row's precomputed view model — see the MVVM note in the frontend guidance. */
type FindingReviewRow = {
  /** Stable key for `v-for`/UListbox. */
  value: string;
  label: string;
  description: string;
  count: number;
  /**
   * Whole-row navigation via `ListboxItem.onSelect` rather than an `<a>` nested inside the
   * option — a listbox option already has `role="option"`, and a nested interactive element
   * (a link) inside it is an invalid/confusing ARIA pattern. `onSelect` fires for both click
   * and keyboard (Enter/Space) activation, opening the target in a new tab so the drill-down
   * list stays open behind it for browsing more rows.
   */
  onSelect: () => void;
};

const open = defineModel<boolean>("open", { required: true });

const props = defineProps<{
  /** Severity the panel activates when it opens; the user can switch tabs after that. */
  initialSeverity: FindingSeverity;
  /** Dashboard's shared window, used as this panel's default — never written back to it. */
  parentWindow: MetricWindowToken;
  items: Record<FindingSeverity, FindingReviewListItemResponse[]>;
  nextCursor: Record<FindingSeverity, string | null>;
  loadingBySeverity: Record<FindingSeverity, boolean>;
  loadingMoreBySeverity: Record<FindingSeverity, boolean>;
}>();

const emits = defineEmits<{
  /** Fired on open and whenever the active severity or local window changes. */
  load: [severity: FindingSeverity, window: MetricWindowToken];
  loadMore: [severity: FindingSeverity, window: MetricWindowToken];
}>();

/** Backing refs for the tab/period selectors — see the computed setters below. */
const internalSeverity = ref<FindingSeverity>(props.initialSeverity);
const internalWindow = ref<MetricWindowToken>(props.parentWindow);

/**
 * User/repo filters — client-side only, over whatever page(s) are currently loaded for the
 * active severity. Unlike severity/window, these never trigger a `load` (no server-side
 * filtering support here), so they're plain refs rather than the emitting computed pattern
 * used for severity/window.
 */
const selectedAuthors = ref<string[]>([]);
const selectedRepos = ref<string[]>([]);

const severityTabItems = [
  { label: "Critical", value: findingSeverityEnum.Critical },
  { label: "Major", value: findingSeverityEnum.Major },
];

/**
 * Emits `load` only on user-driven tab switches, not on the reset-to-default in the open
 * watcher. Also clears the user/repo filters — they're client-side over the active severity's
 * loaded rows, so carrying a selection across severities can silently empty an otherwise
 * populated tab if that user/repo never appears in the new severity's pages.
 */
const activeSeverity = computed<FindingSeverity>({
  get: () => internalSeverity.value,
  set: (value) => {
    internalSeverity.value = value;
    selectedAuthors.value = [];
    selectedRepos.value = [];
    emitLoad();
  },
});

/** Local period override; emits `load` on user-driven change only — see {@link activeSeverity}. */
const activeWindow = computed<MetricWindowToken>({
  get: () => internalWindow.value,
  set: (value) => {
    internalWindow.value = value;
    emitLoad();
  },
});

function emitLoad() {
  emits("load", internalSeverity.value, internalWindow.value);
}

// Reset to the card that was clicked and the dashboard's current window each time the panel
// opens, then trigger the first load — a plain assignment (not the computed setters above) so
// this doesn't fight with a user's in-progress tab/period choice from the prior open.
watch(open, (isOpen) => {
  if (!isOpen) {
    return;
  }
  internalSeverity.value = props.initialSeverity;
  internalWindow.value = props.parentWindow;
  selectedAuthors.value = [];
  selectedRepos.value = [];
  emitLoad();
});

const severityLabel = computed(() =>
  activeSeverity.value === findingSeverityEnum.Major ? "major" : "critical",
);

/**
 * Matches the Critical=error/Major=warning convention already used by the standalone review's
 * severity tabs (`CodeReviewFacetTabs.vue`) — both severities sharing the same red would make a
 * Major-findings view visually indistinguishable from a Critical one.
 */
const severityColor = computed<"error" | "warning">(() =>
  activeSeverity.value === findingSeverityEnum.Major ? "warning" : "error",
);

const title = computed(
  () => `${severityLabel.value === "major" ? "Major" : "Critical"} findings`,
);
const description = computed(
  () =>
    `Reviews with at least one ${severityLabel.value} finding (user and repo filter are over client loaded set).`,
);

const loading = computed(
  () => props.loadingBySeverity[activeSeverity.value] ?? false,
);
const loadingMore = computed(
  () => props.loadingMoreBySeverity[activeSeverity.value] ?? false,
);
const hasMore = computed(() => !!props.nextCursor[activeSeverity.value]);

/** Currently loaded page(s) for the active severity — the base set both the filters and rows draw from. */
const activeItems = computed(() => props.items[activeSeverity.value]);

/** Distinct author logins across the loaded page(s), for the user filter's options. */
const authorItems = computed(() =>
  distinct(activeItems.value.map((item) => item.authorLogin)),
);

/** Distinct bare repo names across the loaded page(s), for the repo filter's options. */
const repoItems = computed(() =>
  distinct(
    activeItems.value.map((item) => bareRepoName(item.ownerQualifiedRepoName)),
  ),
);

/** Precomputes each row's label/description/count so the template stays logic-free. */
const rows = computed<FindingReviewRow[]>(() =>
  activeItems.value
    .filter(
      (item) =>
        (selectedAuthors.value.length === 0 ||
          selectedAuthors.value.includes(item.authorLogin)) &&
        (selectedRepos.value.length === 0 ||
          selectedRepos.value.includes(
            bareRepoName(item.ownerQualifiedRepoName),
          )),
    )
    .map((item) => ({
      value: item.reviewId,
      label: item.title,
      description: `${repoLabel(item)} · ${item.authorLogin} · ${useTimeAgo(new Date(item.createdAtUtc)).value}`,
      count:
        activeSeverity.value === findingSeverityEnum.Major
          ? Math.round(toMetricNumber(item.groupMajorFindings))
          : Math.round(toMetricNumber(item.groupCriticalFindings)),
      onSelect: () => window.open(item.url, "_blank", "noopener"),
    })),
);

/** Distinct values, order-preserving (first occurrence wins). */
function distinct(values: string[]): string[] {
  return [...new Set(values)];
}

/** "repo #123" for PR-backed reviews (owner segment dropped); agent reviews have no PR number. */
function repoLabel(item: FindingReviewListItemResponse): string {
  const repo = bareRepoName(item.ownerQualifiedRepoName);
  return item.requestOrigin === codeReviewRequestOriginEnum.Agent
    ? repo
    : `${repo} #${item.pullRequestNumber}`;
}

/** Drops the "owner/" prefix from an "owner/repo" qualified name. */
function bareRepoName(ownerQualifiedRepoName: string): string {
  const slashIndex = ownerQualifiedRepoName.indexOf("/");
  return slashIndex >= 0 && slashIndex < ownerQualifiedRepoName.length - 1
    ? ownerQualifiedRepoName.slice(slashIndex + 1)
    : ownerQualifiedRepoName;
}
</script>
