import { defineStore, acceptHMRUpdate } from "pinia";
import { useStorage } from "@vueuse/core";
import {
  CodeReviews,
  codeReviewFileNameMatchTypeEnum,
  codeReviewInboxScopeEnum,
  codeReviewModelTierEnum,
  type CodeReviewFileFilterDto,
  type CodeReviewFileMatchCriteriaDto,
  type CodeReviewFindingsResponse,
  type CodeReviewInboxUpdateDto,
  type CodeReviewPullRequestDto,
  type CodeReviewRecordDto,
  type CodeReviewSingleViewResponse,
  type CodeReviewSingleViewMode,
  type CodeReviewRepositoryConfigurationDto,
  type CodeReviewStreamCursorDto,
  type CodeReviewUpdateCursorDto,
  type CodeReviewerActivationConfigurationDto,
  type CodeReviewerAgentDto,
  type CodeReviewerAgentTemplateDto,
  type CodeReviewModelTier,
  type CreateCodeReviewerAgentRequest,
  type SaveCodeReviewRepositoryConfigurationRequest,
  type UpdateCodeReviewerAgentRequest,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import {
  useGitHubSettingsStore,
  type GitHubConfiguredRepository,
} from "@/stores/github-settings-store";
import { useOrganizationSettingsStore } from "@/stores/organization-settings-store";

export const defaultReviewFacet = "";
export const defaultAgentPrompt =
  "Review the pull request for correctness, maintainability, and alignment with the repository guidance.";
export const matchTypeItems = [
  { label: "Exact path", value: codeReviewFileNameMatchTypeEnum.ExactPath },
  { label: "Path prefix", value: codeReviewFileNameMatchTypeEnum.PathPrefix },
  { label: "Extension", value: codeReviewFileNameMatchTypeEnum.Extension },
  { label: "Glob", value: codeReviewFileNameMatchTypeEnum.Glob },
];
export const modelTierItems = [
  { label: "Fast", value: codeReviewModelTierEnum.Fast },
  { label: "High", value: codeReviewModelTierEnum.High },
  { label: "Max", value: codeReviewModelTierEnum.Max },
];

/**
 * Manage Agents right-pane selection ids. Shared between `CodeReviews.vue`
 * (toolbar buttons) and `ManageAgents.vue` (body panels) so both can drive
 * which panel is showing without either owning the other's local state.
 */
export const managementFiltersItemId = "__repository_filters__";
export const managementConfigItemId = "__agent_config__";

type LastSelectedRepositoryIdsByOrganization = Record<string, string>;

/**
 * Editable reviewer-agent form used by the root Manage Agents view.
 *
 * The store owns conversion to generated request DTOs so child panels can stay
 * focused on inputs and emits rather than API payload details.
 */
export type CodeReviewerAgentForm = {
  displayName: string;
  reviewFacet: string;
  modelTier: CodeReviewModelTier;
  prompt: string;
  enabled: boolean;
  activationConfiguration: CodeReviewerActivationConfigurationDto;
};

export type PullRequestInboxUiState = {
  firstSeenAtUtc: Date;
  newAtUtc: Date | null;
  unreadAtUtc: Date | null;
  unreadReviewCount: number;
};

/**
 * Store for the Code Reviews product area.
 *
 * Root views call these actions and pass data to children.  The store keeps
 * generated API access centralized because the inbox, review-history, and agent
 * management screens all depend on the active organization and repository set.
 */
export const useCodeReviewStore = defineStore("code-review-store", () => {
  const appStore = useAppStore();
  const githubSettingsStore = useGitHubSettingsStore();
  const organizationSettingsStore = useOrganizationSettingsStore();

  const activeOrganizationId = computed(
    () =>
      appStore.currentOrganization?.id ?? appStore.user?.organizationId ?? "",
  );
  const lastSelectedRepositoryIdsByOrganization =
    useStorage<LastSelectedRepositoryIdsByOrganization>(
      "zeeq:code-review:last-repository-by-org",
      {},
    );
  /** Last selected repository is a browser-local preference scoped per organization. */
  const selectedRepositoryId = computed<string | null>({
    get: () => {
      const organizationId = activeOrganizationId.value;
      return organizationId
        ? (lastSelectedRepositoryIdsByOrganization.value[organizationId] ??
            null)
        : null;
    },
    set: (repositoryId) => {
      const organizationId = activeOrganizationId.value;
      if (!organizationId) {
        return;
      }

      if (repositoryId) {
        lastSelectedRepositoryIdsByOrganization.value = {
          ...lastSelectedRepositoryIdsByOrganization.value,
          [organizationId]: repositoryId,
        };
        return;
      }

      const next = { ...lastSelectedRepositoryIdsByOrganization.value };
      delete next[organizationId];
      lastSelectedRepositoryIdsByOrganization.value = next;
    },
  });
  const pullRequests = ref<CodeReviewPullRequestDto[]>([]);
  const pullRequestNextCursor = ref<CodeReviewStreamCursorDto | null>(null);
  const pullRequestPollCursor = ref<CodeReviewStreamCursorDto | null>(null);
  const pullRequestPollingInitialized = ref(false);
  const reviewUpdatesCursor = ref<CodeReviewUpdateCursorDto | null>(null);
  const selectedPullRequest = ref<CodeReviewPullRequestDto | null>(null);
  const selectedPullRequestReviews = ref<CodeReviewRecordDto[]>([]);
  const selectedPullRequestReviewsNextCursor =
    ref<CodeReviewStreamCursorDto | null>(null);
  const reviewFindingsByReviewKey = ref<
    Record<string, CodeReviewFindingsResponse>
  >({});
  const loadingReviewFindingsByReviewKey = ref<Record<string, boolean>>({});
  const reviewFindingsErrorsByReviewKey = ref<Record<string, string>>({});
  const latestReviewUpdatesByPullRequestId = ref<
    Record<string, CodeReviewInboxUpdateDto>
  >({});
  const pullRequestUiStateById = ref<Record<string, PullRequestInboxUiState>>(
    {},
  );
  const repositoryConfiguration =
    ref<CodeReviewRepositoryConfigurationDto | null>(null);
  const agents = ref<CodeReviewerAgentDto[]>([]);
  /** Manage Agents right-pane selection: an agent id, or one of the management item ids. */
  const selectedManagementItemId = ref<string>(managementFiltersItemId);
  /**
   * Agent being edited in the Manage Agents config panel, or null when the
   * panel is in create mode. Lives here (not on ManageAgents.vue) because the
   * toolbar in CodeReviews.vue can also open the create panel and must reset it.
   */
  const editingManagementAgent = ref<CodeReviewerAgentDto | null>(null);
  /** Draft seeded from a template or a copied agent, applied when the create panel next opens. */
  const copiedManagementAgentForm = ref<CodeReviewerAgentForm | null>(null);
  /** Monotonic trigger for ManageAgents.vue to open create-mode companion UI. */
  const createManagementAgentRequestId = ref(0);
  /** Last create-mode companion UI request consumed by ManageAgents.vue. */
  const handledCreateManagementAgentRequestId = ref(0);
  const loadingRepositories = ref(false);
  const loadingPullRequests = ref(false);
  const loadingSelectedPullRequest = ref(false);
  const pollingPullRequests = ref(false);
  const pollingReviewUpdates = ref(false);
  const loadingAgents = ref(false);
  const savingAgent = ref(false);
  const savingRepositoryConfiguration = ref(false);
  const requestingReviewId = ref<string | null>(null);
  const singleReview = ref<CodeReviewRecordDto | null>(null);
  const singleReviewReviews = ref<CodeReviewRecordDto[]>([]);
  const singleReviewMode = ref<CodeReviewSingleViewMode | null>(null);
  const singleReviewPullRequest = ref<CodeReviewPullRequestDto | null>(null);
  const loadingSingleReview = ref(false);

  // Single PR standalone view (Mode 1) — populated by loadSinglePullRequest.
  const singlePullRequest = ref<CodeReviewPullRequestDto | null>(null);
  const singlePullRequestReviews = ref<CodeReviewRecordDto[]>([]);
  const singlePullRequestNextCursor = ref<CodeReviewStreamCursorDto | null>(
    null,
  );
  const loadingSinglePullRequest = ref(false);
  const pollingSinglePullRequestReviews = ref(false);
  const error = ref<string | null>(null);

  const configuredRepositories = computed<GitHubConfiguredRepository[]>(
    () => githubSettingsStore.configuredRepositories,
  );
  const webhookEnabledRepositories = computed<GitHubConfiguredRepository[]>(
    () =>
      configuredRepositories.value.filter((repository) => repository.enabled),
  );
  const selectedRepository = computed<GitHubConfiguredRepository | null>(
    () =>
      webhookEnabledRepositories.value.find(
        (repository) => repository.id === selectedRepositoryId.value,
      ) ?? null,
  );
  const hasConfiguredRepositories = computed(
    () => webhookEnabledRepositories.value.length > 0,
  );
  const hasUnreadPullRequestUpdates = computed(() =>
    Object.values(pullRequestUiStateById.value).some(
      (state) => state.unreadAtUtc !== null,
    ),
  );

  const maxRepositoryAgents = 10;
  const hasReachedAgentLimit = computed(
    () => agents.value.length >= maxRepositoryAgents,
  );
  /** Guards the Manage Agents "New agent" trigger, wherever it's rendered. */
  const canCreateManagementAgent = computed(
    () =>
      organizationSettingsStore.canManageOrganization &&
      Boolean(selectedRepositoryId.value) &&
      !hasReachedAgentLimit.value,
  );
  const newAgentButtonTitle = computed(() =>
    hasReachedAgentLimit.value
      ? "This repository already has 10 reviewer agents."
      : undefined,
  );

  /**
   * Loads configured repositories and the first inbox page for the active org.
   * Repository selection is optional so users can review all configured repos.
   */
  async function loadInbox() {
    await loadConfiguredRepositories();
    await loadPullRequests({ reset: true });
  }

  /** Loads GitHub repository mappings without exposing GitHub settings actions. */
  async function loadConfiguredRepositories() {
    loadingRepositories.value = true;
    error.value = null;

    try {
      await githubSettingsStore.loadConfiguredRepositories();

      if (
        selectedRepositoryId.value &&
        !webhookEnabledRepositories.value.some(
          (repository) => repository.id === selectedRepositoryId.value,
        )
      ) {
        selectedRepositoryId.value = null;
      }

      if (!selectedRepositoryId.value) {
        selectedRepositoryId.value =
          webhookEnabledRepositories.value[0]?.id ?? null;
      }
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load repositories.");
      throw err;
    } finally {
      loadingRepositories.value = false;
    }
  }

  /**
   * Loads the inbox page.  A reset also refreshes the update-feed cursor used
   * for later lightweight polling.
   */
  async function loadPullRequests(options: { reset: boolean }) {
    const orgId = requireOrganizationId();
    loadingPullRequests.value = true;
    error.value = null;

    try {
      if (options.reset) {
        latestReviewUpdatesByPullRequestId.value = {};
        reviewUpdatesCursor.value = null;
        pullRequestPollCursor.value = null;
        pullRequestPollingInitialized.value = false;
        pullRequestUiStateById.value = {};
        clearReviewFindingsState();
      }

      const cursor = options.reset ? null : pullRequestNextCursor.value;
      const response = await CodeReviews.listCodeReviewPullRequests(orgId, {
        repositoryId: selectedRepositoryId.value ?? undefined,
        cursorCreatedAtUtc: cursor?.createdAtUtc,
        cursorId: cursor?.id,
        pageSize: 25,
      });

      if (options.reset) {
        pullRequests.value = response.items;
      } else {
        mergePullRequestRows(response.items);
      }

      touchPullRequestRows(response.items);
      pullRequestNextCursor.value = response.nextCursor;
      pullRequestPollCursor.value =
        response.newestCursor ?? pullRequestPollCursor.value;
      pullRequestPollingInitialized.value = true;
      reviewUpdatesCursor.value =
        response.reviewUpdatesCursor ?? reviewUpdatesCursor.value;

      if (!selectedPullRequest.value && pullRequests.value.length > 0) {
        await selectPullRequest(pullRequests.value[0]);
      }
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load pull requests.");
      throw err;
    } finally {
      loadingPullRequests.value = false;
    }
  }

  /**
   * Selects one PR and loads its detail plus newest review history.
   *
   * @param pullRequest - Inbox row selected by the user.
   */
  async function selectPullRequest(pullRequest: CodeReviewPullRequestDto) {
    const orgId = requireOrganizationId();
    loadingSelectedPullRequest.value = true;
    error.value = null;

    try {
      const detail = await CodeReviews.getCodeReviewPullRequest(
        pullRequest.id,
        orgId,
        { c: pullRequest.singleViewToken },
      );
      const reviews = await CodeReviews.listPullRequestCodeReviews(
        pullRequest.id,
        orgId,
        {
          createdAtUtc: detail.pullRequest.createdAtUtc,
          pageSize: 20,
        },
      );

      selectedPullRequest.value = detail.pullRequest;
      selectedPullRequestReviews.value = reviews.items;
      selectedPullRequestReviewsNextCursor.value = reviews.nextCursor;
      markPullRequestRead(pullRequest.id);
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load pull request details.");
      throw err;
    } finally {
      loadingSelectedPullRequest.value = false;
    }
  }

  /** Requests a fresh review for the selected PR and patches local rows. */
  async function requestReview(pullRequest: CodeReviewPullRequestDto) {
    const orgId = requireOrganizationId();
    requestingReviewId.value = pullRequest.id;
    error.value = null;

    try {
      const response = await CodeReviews.requestCodeReview(
        pullRequest.id,
        orgId,
        {
          createdAtUtc: pullRequest.createdAtUtc,
        },
      );

      patchPullRequest(response.pullRequest);

      if (response.codeReview) {
        upsertReview(response.codeReview);
        setLatestReviewUpdate(response.pullRequest, response.codeReview);
      }

      return response;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not request a code review.");
      throw err;
    } finally {
      requestingReviewId.value = null;
    }
  }

  /** Loads one review plus its related set for the single-review view. */
  async function loadSingleReview(
    reviewId: string,
    token: string,
  ): Promise<CodeReviewSingleViewResponse> {
    const orgId = requireOrganizationId();
    loadingSingleReview.value = true;
    error.value = null;

    try {
      const response = await CodeReviews.getCodeReview(reviewId, orgId, {
        c: token,
      });
      singleReview.value = response.review;
      singleReviewReviews.value = response.reviews;
      singleReviewMode.value = response.mode;
      singleReviewPullRequest.value = response.pullRequest;
      return response;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load code review.");
      throw err;
    } finally {
      loadingSingleReview.value = false;
    }
  }

  /**
   * Loads one PR and its newest reviews for the standalone single-PR view (Mode 1).
   *
   * Keyed by (recordId, singleViewToken) — the token is minted server-side and
   * carried on every PR DTO. Writes to dedicated singlePullRequest* refs so the
   * inbox state is not disturbed. Findings lazy-load per review via loadReviewFindings.
   */
  async function loadSinglePullRequest(
    recordId: string,
    singleViewToken: string,
  ): Promise<void> {
    const orgId = requireOrganizationId();
    loadingSinglePullRequest.value = true;
    singlePullRequest.value = null;
    singlePullRequestReviews.value = [];
    singlePullRequestNextCursor.value = null;
    error.value = null;

    try {
      // NOTE: Two round-trips (detail + reviews). If latency matters, add a bundling
      // endpoint GET /pull-requests/{id}/single that stitches PR record + review page
      // into one response, then collapse these two calls into one.
      const detail = await CodeReviews.getCodeReviewPullRequest(
        recordId,
        orgId,
        {
          c: singleViewToken,
        },
      );
      const reviews = await CodeReviews.listPullRequestCodeReviews(
        recordId,
        orgId,
        {
          createdAtUtc: detail.pullRequest.createdAtUtc,
          pageSize: 20,
        },
      );

      singlePullRequest.value = detail.pullRequest;
      singlePullRequestReviews.value = reviews.items;
      singlePullRequestNextCursor.value = reviews.nextCursor;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load pull request.");
      throw err;
    } finally {
      loadingSinglePullRequest.value = false;
    }
  }

  /**
   * Polls the newest reviews for the standalone single-PR view (Mode 1) and
   * merges any new/updated rows into `singlePullRequestReviews`.
   *
   * Mirrors `refreshSelectedPullRequestReviews`'s upsert-by-id merge, but reads
   * `singlePullRequest`/`singlePullRequestReviews` so it doesn't disturb inbox
   * state and keeps working from a shared single-view link with no inbox context.
   */
  async function pollSinglePullRequestReviews() {
    const pullRequest = singlePullRequest.value;

    if (!pullRequest || pollingSinglePullRequestReviews.value) {
      return;
    }

    const orgId = requireOrganizationId();
    const requestedPullRequestId = pullRequest.id;
    pollingSinglePullRequestReviews.value = true;

    try {
      const reviews = await CodeReviews.listPullRequestCodeReviews(
        pullRequest.id,
        orgId,
        {
          createdAtUtc: pullRequest.createdAtUtc,
          pageSize: 20,
        },
      );

      // NOTE: Not reachable today (this view has no route-param watcher, so
      // singlePullRequest is never reassigned to a different PR while mounted),
      // but this poll spans an await and the standalone view could later gain
      // in-place PR navigation. Guard defensively so a stale response can't
      // merge reviews into whatever PR is selected by the time it resolves.
      if (singlePullRequest.value?.id !== requestedPullRequestId) {
        return false;
      }

      const knownIds = new Set(
        singlePullRequestReviews.value.map((review) => review.id),
      );

      for (const review of [...reviews.items].reverse()) {
        upsertSinglePullRequestReview(review);
      }

      // NOTE: Deliberately not touching singlePullRequestNextCursor here. This
      // poll always re-fetches the newest page to catch new/updated reviews;
      // reassigning the cursor from that response would clobber pagination
      // progress from a (future) load-older-reviews action. The cursor is
      // owned solely by loadSinglePullRequest's initial load.
      return reviews.items.some((review) => !knownIds.has(review.id));
    } finally {
      pollingSinglePullRequestReviews.value = false;
    }
  }

  function upsertSinglePullRequestReview(review: CodeReviewRecordDto) {
    const withoutExisting = singlePullRequestReviews.value.filter(
      (item) => item.id !== review.id,
    );

    singlePullRequestReviews.value = [review, ...withoutExisting];
  }

  /**
   * Resolves a PR by repo-scoped provider number, injects it into the inbox
   * list, and selects it (Mode 2 deep-link / inbox number lookup).
   *
   * Requires a repository because PR numbers are not unique across repositories.
   * The resolved row is merged/deduped into the inbox list so it appears highlighted.
   * Review loading reuses the existing selectPullRequest path.
   *
   * @param repositoryId - Repository scope for the number lookup (required).
   * @param prNumber - Provider PR number within the repository (e.g. GitHub #42).
   */
  async function findAndSelectPullRequestByNumber(
    repositoryId: string,
    prNumber: number,
  ): Promise<void> {
    const orgId = requireOrganizationId();

    try {
      const detail = await CodeReviews.getPullRequestByNumber(orgId, {
        repositoryId,
        pullRequestNumber: prNumber,
      });

      // Inject/dedup the resolved row into the inbox list, then select it.
      mergePullRequestRows([detail.pullRequest]);
      touchPullRequestRows([detail.pullRequest]);
      await selectPullRequest(detail.pullRequest);
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not find pull request.");
      throw err;
    }
  }

  /** Loads parsed finding bodies for one review artifact on demand. */
  async function loadReviewFindings(review: CodeReviewRecordDto) {
    const key = reviewFindingsKey(review);

    if (reviewFindingsByReviewKey.value[key]) {
      return reviewFindingsByReviewKey.value[key];
    }

    // Fetch when there is anything to show (findings OR source telemetry). A clean
    // review that still consulted docs must hydrate so the sources panel can render.
    if (totalFindings(review) === 0 && !review.hasSourceTelemetry) {
      const emptyFindings: CodeReviewFindingsResponse = {
        codeReviewRecordId: review.id,
        codeReviewCreatedAtUtc: review.createdAtUtc,
        noAgentsActivated: false,
        reviews: [],
        sourceTelemetry: null,
      };
      reviewFindingsByReviewKey.value = {
        ...reviewFindingsByReviewKey.value,
        [key]: emptyFindings,
      };

      return emptyFindings;
    }

    const orgId = requireOrganizationId();
    setReviewFindingsLoading(key, true);
    setReviewFindingsError(key, null);

    try {
      const response = await CodeReviews.getCodeReviewFindings(
        review.id,
        orgId,
        {
          createdAtUtc: review.createdAtUtc,
        },
      );
      reviewFindingsByReviewKey.value = {
        ...reviewFindingsByReviewKey.value,
        [key]: response,
      };

      return response;
    } catch (err: unknown) {
      const message = errorMessage(err, "Could not load review findings.");
      setReviewFindingsError(key, message);
      throw err;
    } finally {
      setReviewFindingsLoading(key, false);
    }
  }

  /**
   * Polls the newest PR page and marks only rows created after the stored
   * high-water cursor as new.
   */
  async function pollPullRequestUpdates() {
    if (!pullRequestPollingInitialized.value || pollingPullRequests.value) {
      return;
    }

    const orgId = requireOrganizationId();
    const previousCursor = pullRequestPollCursor.value;
    pollingPullRequests.value = true;

    try {
      const response = await CodeReviews.listCodeReviewPullRequests(orgId, {
        repositoryId: selectedRepositoryId.value ?? undefined,
        pageSize: 50,
      });

      const selectedPullRequestUpdated = mergePolledPullRequests(
        response.items,
        previousCursor,
      );
      pullRequestPollCursor.value =
        response.newestCursor ?? pullRequestPollCursor.value;
      reviewUpdatesCursor.value =
        reviewUpdatesCursor.value ?? response.reviewUpdatesCursor;

      if (selectedPullRequestUpdated) {
        await refreshSelectedPullRequestReviews();
      }
    } finally {
      pollingPullRequests.value = false;
    }
  }

  /**
   * Polls minimal review updates and patches visible rows without loading every
   * selected PR's full review history.
   */
  async function pollInboxUpdates() {
    const orgId = requireOrganizationId();
    const cursor = reviewUpdatesCursor.value;

    if (!cursor || pollingReviewUpdates.value) {
      return;
    }

    pollingReviewUpdates.value = true;

    try {
      const scope = codeReviewInboxScopeEnum.All;
      const cursorForScope = cursor.scope === scope ? cursor : null;
      const response = await CodeReviews.listCodeReviewInboxUpdates(orgId, {
        repositoryId: selectedRepositoryId.value ?? undefined,
        scope,
        reviewCreatedAtLowerBoundUtc: cursor.reviewCreatedAtLowerBoundUtc,
        cursorUpdatedAtUtc: cursorForScope?.updatedAtUtc,
        cursorCreatedAtUtc: cursorForScope?.createdAtUtc,
        cursorId: cursorForScope?.id,
        cursorTeamId: cursorForScope?.teamId ?? undefined,
        cursorRepositoryId: cursorForScope?.repositoryId ?? undefined,
        cursorScope: cursorForScope?.scope,
        cursorSubjectUserId: cursorForScope?.subjectUserId ?? undefined,
        pageSize: 50,
      });
      let refreshSelectedReviews = false;

      for (const update of response.items) {
        patchReviewUpdate(update, { markUnread: true });

        if (selectedPullRequest.value?.id === update.pullRequestRecordId) {
          refreshSelectedReviews = true;
        }
      }

      reviewUpdatesCursor.value =
        response.nextCursor ??
        response.newestCursor ??
        reviewUpdatesCursor.value;

      if (refreshSelectedReviews) {
        await refreshSelectedPullRequestReviews();
      }
    } finally {
      pollingReviewUpdates.value = false;
    }
  }

  /** Loads agents and repository filters for the selected repository. */
  async function loadAgentManagement() {
    await loadConfiguredRepositories();

    if (!selectedRepositoryId.value) {
      selectedRepositoryId.value =
        webhookEnabledRepositories.value[0]?.id ?? null;
    }

    await loadSelectedRepositoryManagement();
  }

  /** Changes the repository under management and reloads its settings. */
  async function setSelectedRepository(repositoryId: string | null) {
    selectedRepositoryId.value = repositoryId;
    selectedPullRequest.value = null;
    selectedPullRequestReviews.value = [];
    pullRequestUiStateById.value = {};
    clearReviewFindingsState();
    await loadSelectedRepositoryManagement();
  }

  /** Loads only the selected repository's agent and filter state. */
  async function loadSelectedRepositoryManagement() {
    const repositoryId = selectedRepositoryId.value;

    if (!repositoryId) {
      agents.value = [];
      repositoryConfiguration.value = null;
      return;
    }

    const orgId = requireOrganizationId();
    loadingAgents.value = true;
    error.value = null;

    try {
      const [agentResponse, configurationResponse] = await Promise.all([
        CodeReviews.listRepositoryCodeReviewerAgents(repositoryId, orgId),
        CodeReviews.getCodeReviewRepositoryConfiguration(repositoryId, orgId),
      ]);

      agents.value = agentResponse.items;
      repositoryConfiguration.value = configurationResponse.configuration;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load reviewer agents.");
      throw err;
    } finally {
      loadingAgents.value = false;
    }
  }

  /**
   * Persists repository-level file filters.
   *
   * @param fileFilter - Include/exclude filters applied before agent activation.
   */
  async function saveRepositoryFileFilter(fileFilter: CodeReviewFileFilterDto) {
    await saveRepositoryConfiguration({
      ...repositoryConfiguration.value,
      fileFilter,
    });
  }

  /**
   * Persists the repository-level shared prompt fragment injected into every
   * reviewer agent's prompt for this repository.
   *
   * @param sharedPromptFragment - Organization-authored guidance text.
   */
  async function saveSharedPromptFragment(sharedPromptFragment: string) {
    await saveRepositoryConfiguration({
      ...repositoryConfiguration.value,
      fileFilter:
        repositoryConfiguration.value?.fileFilter ?? emptyFileFilter(),
      sharedPromptFragment,
    });
  }

  /** Switches the Manage Agents right pane to the repository file-filters panel. */
  function selectManagementFilters() {
    editingManagementAgent.value = null;
    selectedManagementItemId.value = managementFiltersItemId;
  }

  /** Opens the Manage Agents config panel with an empty create draft. */
  function openCreateAgentPanel() {
    if (!canCreateManagementAgent.value) {
      return;
    }

    copiedManagementAgentForm.value = null;
    editingManagementAgent.value = null;
    selectedManagementItemId.value = managementConfigItemId;
    createManagementAgentRequestId.value += 1;
  }

  /**
   * Persists the full repository review configuration, preserving any
   * existing check-run settings when the caller omits them.
   */
  async function saveRepositoryConfiguration(
    configuration: CodeReviewRepositoryConfigurationDto,
  ) {
    const repositoryId = requireRepositoryId();
    const orgId = requireOrganizationId();
    const request: SaveCodeReviewRepositoryConfigurationRequest = {
      configuration,
    };

    savingRepositoryConfiguration.value = true;
    error.value = null;

    try {
      const response = await CodeReviews.saveCodeReviewRepositoryConfiguration(
        repositoryId,
        orgId,
        request,
      );
      repositoryConfiguration.value = response.configuration;
    } catch (err: unknown) {
      error.value = errorMessage(
        err,
        "Could not save repository configuration.",
      );
      throw err;
    } finally {
      savingRepositoryConfiguration.value = false;
    }
  }

  /**
   * Lists reviewer agents for any repository without mutating the active list.
   *
   * Powers the create panel's copy-from picker so a new agent can be seeded
   * from an agent configured in a different repository across the org.
   */
  async function listRepositoryAgents(
    repositoryId: string,
  ): Promise<CodeReviewerAgentDto[]> {
    const orgId = requireOrganizationId();
    const response = await CodeReviews.listRepositoryCodeReviewerAgents(
      repositoryId,
      orgId,
    );

    return response.items;
  }

  /**
   * Lists the built-in, clonable reviewer-agent templates for the active org.
   *
   * Templates are code-defined starting points seeded into a new agent's form;
   * they are org-scoped for auth only and share no persisted-agent identity.
   */
  async function listAgentTemplates(): Promise<CodeReviewerAgentTemplateDto[]> {
    const orgId = requireOrganizationId();
    const response = await CodeReviews.listCodeReviewerAgentTemplates(orgId);

    return response.items;
  }

  /** Creates a persisted reviewer agent for the selected repository. */
  async function createAgent(form: CodeReviewerAgentForm) {
    const repositoryId = requireRepositoryId();
    const orgId = requireOrganizationId();
    const request: CreateCodeReviewerAgentRequest = agentFormToRequest(form);

    savingAgent.value = true;
    error.value = null;

    try {
      const response = await CodeReviews.createRepositoryCodeReviewerAgent(
        repositoryId,
        orgId,
        request,
      );
      upsertAgent(response.agent);
      return response.agent;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not create reviewer agent.");
      throw err;
    } finally {
      savingAgent.value = false;
    }
  }

  /** Updates editable fields for an existing persisted reviewer agent. */
  async function updateAgent(agentId: string, form: CodeReviewerAgentForm) {
    const repositoryId = requireRepositoryId();
    const orgId = requireOrganizationId();
    const request: UpdateCodeReviewerAgentRequest = agentFormToRequest(form);

    savingAgent.value = true;
    error.value = null;

    try {
      const response = await CodeReviews.updateRepositoryCodeReviewerAgent(
        repositoryId,
        agentId,
        orgId,
        request,
      );
      upsertAgent(response.agent);
      return response.agent;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not update reviewer agent.");
      throw err;
    } finally {
      savingAgent.value = false;
    }
  }

  /** Disables one persisted reviewer agent. */
  async function disableAgent(agentId: string) {
    const repositoryId = requireRepositoryId();
    const orgId = requireOrganizationId();

    savingAgent.value = true;
    error.value = null;

    try {
      await CodeReviews.deleteRepositoryCodeReviewerAgent(
        repositoryId,
        agentId,
        orgId,
      );
      markAgentDisabled(agentId);
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not disable reviewer agent.");
      throw err;
    } finally {
      savingAgent.value = false;
    }
  }

  /** Removes one reviewer agent from the active management list. */
  async function deleteAgent(agentId: string) {
    const repositoryId = requireRepositoryId();
    const orgId = requireOrganizationId();

    savingAgent.value = true;
    error.value = null;

    try {
      await CodeReviews.deleteRepositoryCodeReviewerAgent(
        repositoryId,
        agentId,
        orgId,
      );
      removeAgent(agentId);
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not delete reviewer agent.");
      throw err;
    } finally {
      savingAgent.value = false;
    }
  }

  /** Resets the PR list when the repository filter changes. */
  async function setInboxRepositoryFilter(repositoryId: string | null) {
    selectedRepositoryId.value = repositoryId;
    selectedPullRequest.value = null;
    selectedPullRequestReviews.value = [];
    await loadPullRequests({ reset: true });
  }

  /** Clears frontend-only unread markers without changing API state. */
  function markInboxRead() {
    pullRequestUiStateById.value = Object.fromEntries(
      Object.entries(pullRequestUiStateById.value).map(([id, state]) => [
        id,
        {
          ...state,
          newAtUtc: null,
          unreadAtUtc: null,
          unreadReviewCount: 0,
        },
      ]),
    );
  }

  /** Clears frontend-only unread markers for one selected PR row. */
  function markPullRequestRead(pullRequestId: string) {
    const state = pullRequestUiStateById.value[pullRequestId];

    if (
      !state?.unreadAtUtc &&
      !state?.newAtUtc &&
      state?.unreadReviewCount === 0
    ) {
      return;
    }

    pullRequestUiStateById.value = {
      ...pullRequestUiStateById.value,
      [pullRequestId]: {
        ...(state ?? {
          firstSeenAtUtc: new Date(),
        }),
        newAtUtc: null,
        unreadAtUtc: null,
        unreadReviewCount: 0,
      },
    };
  }

  function requireOrganizationId(): string {
    if (!activeOrganizationId.value) {
      throw new Error("Select an organization before using code reviews.");
    }

    return activeOrganizationId.value;
  }

  function requireRepositoryId(): string {
    if (!selectedRepositoryId.value) {
      throw new Error("Select a repository before managing reviewer agents.");
    }

    return selectedRepositoryId.value;
  }

  function patchPullRequest(pullRequest: CodeReviewPullRequestDto) {
    mergePullRequestRows([pullRequest]);
    touchPullRequestRows([pullRequest]);

    if (selectedPullRequest.value?.id === pullRequest.id) {
      selectedPullRequest.value = pullRequest;
    }
  }

  function patchReviewUpdate(
    update: CodeReviewInboxUpdateDto,
    options: { markUnread: boolean } = { markUnread: false },
  ) {
    latestReviewUpdatesByPullRequestId.value = {
      ...latestReviewUpdatesByPullRequestId.value,
      [update.pullRequestRecordId]: update,
    };

    if (options.markUnread) {
      markPullRequestUpdated(update);
    }

    if (selectedPullRequest.value?.id === update.pullRequestRecordId) {
      const review = selectedPullRequestReviews.value.find(
        (item) => item.id === update.codeReviewRecordId,
      );

      if (review) {
        review.status = update.status;
        review.criticalFindings = update.criticalFindings;
        review.majorFindings = update.majorFindings;
        review.minorFindings = update.minorFindings;
        review.suggestionFindings = update.suggestionFindings;
        review.commentFindings = update.commentFindings;
        review.remainingReviewBudget = update.remainingReviewBudget;
        review.updatedAtUtc = update.updatedAtUtc;
      }
    }
  }

  async function refreshSelectedPullRequestReviews() {
    const pullRequest = selectedPullRequest.value;

    if (!pullRequest) {
      return;
    }

    const orgId = requireOrganizationId();
    const requestedPullRequestId = pullRequest.id;
    const reviews = await CodeReviews.listPullRequestCodeReviews(
      pullRequest.id,
      orgId,
      {
        createdAtUtc: pullRequest.createdAtUtc,
        pageSize: 20,
      },
    );

    // NOTE: The inbox lets the user click a different PR row while this poll-
    // triggered refresh is still in flight, reassigning selectedPullRequest
    // before this await resolves. Discard the response instead of merging
    // reviews for a PR that is no longer selected.
    if (selectedPullRequest.value?.id !== requestedPullRequestId) {
      return;
    }

    for (const review of [...reviews.items].reverse()) {
      upsertReview(review);
    }

    // NOTE: Deliberately not touching selectedPullRequestReviewsNextCursor
    // here. This refresh always re-fetches the newest page to catch new/
    // updated reviews; reassigning the cursor from that response would
    // clobber pagination progress from a (future) load-older-reviews action.
    // The cursor is owned solely by selectPullRequest's initial load.
  }

  function upsertReview(review: CodeReviewRecordDto) {
    const withoutExisting = selectedPullRequestReviews.value.filter(
      (item) => item.id !== review.id,
    );

    selectedPullRequestReviews.value = [review, ...withoutExisting];
  }

  function setLatestReviewUpdate(
    pullRequest: CodeReviewPullRequestDto,
    review: CodeReviewRecordDto,
  ) {
    latestReviewUpdatesByPullRequestId.value = {
      ...latestReviewUpdatesByPullRequestId.value,
      [pullRequest.id]: {
        pullRequestRecordId: pullRequest.id,
        pullRequestCreatedAtUtc: pullRequest.createdAtUtc,
        codeReviewRecordId: review.id,
        codeReviewCreatedAtUtc: review.createdAtUtc,
        status: review.status,
        criticalFindings: review.criticalFindings,
        majorFindings: review.majorFindings,
        minorFindings: review.minorFindings,
        suggestionFindings: review.suggestionFindings,
        commentFindings: review.commentFindings,
        remainingReviewBudget: review.remainingReviewBudget,
        updatedAtUtc: review.updatedAtUtc,
      },
    };
  }

  function mergePolledPullRequests(
    rows: CodeReviewPullRequestDto[],
    previousCursor: CodeReviewStreamCursorDto | null,
  ): boolean {
    const knownById = new Map(
      pullRequests.value.map((item) => [item.id, item]),
    );
    const selectedPullRequestId = selectedPullRequest.value?.id ?? null;
    let selectedPullRequestUpdated = false;
    const now = new Date();

    mergePullRequestRows(rows);

    for (const row of rows) {
      const known = knownById.get(row.id);

      if (
        !known &&
        (!previousCursor || isNewerThanCursor(row, previousCursor))
      ) {
        markPullRequestNew(row, now);
      } else if (known && isPullRequestRowUpdated(row, known)) {
        markPullRequestRowUpdated(row, new Date(row.updatedAtUtc));
        selectedPullRequestUpdated ||= row.id === selectedPullRequestId;
      } else {
        touchPullRequestRows([row]);
      }
    }

    return selectedPullRequestUpdated;
  }

  function mergePullRequestRows(rows: CodeReviewPullRequestDto[]) {
    const byId = new Map<string, CodeReviewPullRequestDto>();

    for (const row of pullRequests.value) {
      byId.set(row.id, row);
    }

    for (const row of rows) {
      byId.set(row.id, row);

      if (selectedPullRequest.value?.id === row.id) {
        selectedPullRequest.value = row;
      }
    }

    pullRequests.value = sortPullRequests([...byId.values()]);
  }

  function touchPullRequestRows(rows: CodeReviewPullRequestDto[]) {
    const nextState = { ...pullRequestUiStateById.value };

    for (const row of rows) {
      nextState[row.id] ??= {
        firstSeenAtUtc: new Date(),
        newAtUtc: null,
        unreadAtUtc: null,
        unreadReviewCount: 0,
      };
    }

    pullRequestUiStateById.value = nextState;
  }

  function markPullRequestNew(
    pullRequest: CodeReviewPullRequestDto,
    timestamp: Date,
  ) {
    pullRequestUiStateById.value = {
      ...pullRequestUiStateById.value,
      [pullRequest.id]: {
        firstSeenAtUtc:
          pullRequestUiStateById.value[pullRequest.id]?.firstSeenAtUtc ??
          timestamp,
        newAtUtc: timestamp,
        unreadAtUtc: timestamp,
        unreadReviewCount:
          pullRequestUiStateById.value[pullRequest.id]?.unreadReviewCount ?? 0,
      },
    };
  }

  function markPullRequestUpdated(update: CodeReviewInboxUpdateDto) {
    const current = pullRequestUiStateById.value[update.pullRequestRecordId];
    const unreadAtUtc = new Date(update.updatedAtUtc);

    pullRequestUiStateById.value = {
      ...pullRequestUiStateById.value,
      [update.pullRequestRecordId]: {
        firstSeenAtUtc: current?.firstSeenAtUtc ?? unreadAtUtc,
        newAtUtc: current?.newAtUtc ?? null,
        unreadAtUtc,
        unreadReviewCount: Math.max(current?.unreadReviewCount ?? 0, 1),
      },
    };
  }

  function markPullRequestRowUpdated(
    pullRequest: CodeReviewPullRequestDto,
    unreadAtUtc: Date,
  ) {
    const current = pullRequestUiStateById.value[pullRequest.id];

    pullRequestUiStateById.value = {
      ...pullRequestUiStateById.value,
      [pullRequest.id]: {
        firstSeenAtUtc: current?.firstSeenAtUtc ?? unreadAtUtc,
        newAtUtc: current?.newAtUtc ?? null,
        unreadAtUtc,
        unreadReviewCount: Math.max(current?.unreadReviewCount ?? 0, 1),
      },
    };
  }

  function clearReviewFindingsState() {
    reviewFindingsByReviewKey.value = {};
    loadingReviewFindingsByReviewKey.value = {};
    reviewFindingsErrorsByReviewKey.value = {};
  }

  function setReviewFindingsLoading(reviewKey: string, loading: boolean) {
    loadingReviewFindingsByReviewKey.value = {
      ...loadingReviewFindingsByReviewKey.value,
      [reviewKey]: loading,
    };
  }

  function setReviewFindingsError(reviewKey: string, message: string | null) {
    const nextErrors = { ...reviewFindingsErrorsByReviewKey.value };

    if (message) {
      nextErrors[reviewKey] = message;
    } else {
      delete nextErrors[reviewKey];
    }

    reviewFindingsErrorsByReviewKey.value = nextErrors;
  }

  function upsertAgent(agent: CodeReviewerAgentDto) {
    const index = agents.value.findIndex((item) => item.id === agent.id);

    if (index >= 0) {
      agents.value[index] = agent;
      return;
    }

    agents.value = [agent, ...agents.value];
  }

  function markAgentDisabled(agentId: string) {
    const agent = agents.value.find((item) => item.id === agentId);

    if (!agent) {
      return;
    }

    agent.enabled = false;
    agent.updatedAtUtc = new Date();
  }

  function removeAgent(agentId: string) {
    agents.value = agents.value.filter((agent) => agent.id !== agentId);
  }

  return {
    selectedRepositoryId,
    pullRequests,
    pullRequestNextCursor,
    pullRequestPollCursor,
    reviewUpdatesCursor,
    selectedPullRequest,
    selectedPullRequestReviews,
    selectedPullRequestReviewsNextCursor,
    reviewFindingsByReviewKey,
    loadingReviewFindingsByReviewKey,
    reviewFindingsErrorsByReviewKey,
    latestReviewUpdatesByPullRequestId,
    pullRequestUiStateById,
    repositoryConfiguration,
    agents,
    selectedManagementItemId,
    editingManagementAgent,
    copiedManagementAgentForm,
    createManagementAgentRequestId,
    handledCreateManagementAgentRequestId,
    hasReachedAgentLimit,
    canCreateManagementAgent,
    newAgentButtonTitle,
    loadingRepositories,
    loadingPullRequests,
    loadingSelectedPullRequest,
    pollingPullRequests,
    pollingReviewUpdates,
    loadingAgents,
    savingAgent,
    savingRepositoryConfiguration,
    requestingReviewId,
    singleReview,
    singleReviewReviews,
    singleReviewMode,
    singleReviewPullRequest,
    loadingSingleReview,
    singlePullRequest,
    singlePullRequestReviews,
    singlePullRequestNextCursor,
    loadingSinglePullRequest,
    pollingSinglePullRequestReviews,
    error,
    configuredRepositories,
    webhookEnabledRepositories,
    selectedRepository,
    activeOrganizationId,
    hasConfiguredRepositories,
    hasUnreadPullRequestUpdates,
    loadInbox,
    loadConfiguredRepositories,
    loadPullRequests,
    selectPullRequest,
    requestReview,
    loadReviewFindings,
    pollPullRequestUpdates,
    pollInboxUpdates,
    pollSinglePullRequestReviews,
    loadAgentManagement,
    setSelectedRepository,
    loadSelectedRepositoryManagement,
    saveRepositoryFileFilter,
    saveSharedPromptFragment,
    saveRepositoryConfiguration,
    selectManagementFilters,
    openCreateAgentPanel,
    listRepositoryAgents,
    listAgentTemplates,
    createAgent,
    updateAgent,
    disableAgent,
    deleteAgent,
    setInboxRepositoryFilter,
    loadSingleReview,
    loadSinglePullRequest,
    findAndSelectPullRequestByNumber,
    markInboxRead,
  };
});

/** Builds the default agent form used by create and edit panels. */
export function defaultAgentForm(): CodeReviewerAgentForm {
  return {
    displayName: "",
    reviewFacet: defaultReviewFacet,
    modelTier: codeReviewModelTierEnum.Fast,
    prompt: defaultAgentPrompt,
    enabled: true,
    activationConfiguration: emptyActivationConfiguration(),
  };
}

/** Converts a persisted agent into an editable form copy. */
export function agentToForm(
  agent: CodeReviewerAgentDto,
): CodeReviewerAgentForm {
  return {
    displayName: agent.displayName,
    reviewFacet: agent.reviewFacet,
    modelTier: agent.modelTier,
    prompt: agent.prompt,
    enabled: agent.enabled,
    activationConfiguration: cloneActivationConfiguration(
      agent.activationConfiguration,
    ),
  };
}

/**
 * Converts a built-in template into an editable form copy for a new agent.
 *
 * The template's activation config is deep-cloned so form edits never mutate
 * shared references. The result always starts enabled, matching a fresh draft.
 */
export function templateToForm(
  template: CodeReviewerAgentTemplateDto,
): CodeReviewerAgentForm {
  return {
    displayName: template.displayName,
    reviewFacet: template.reviewFacet,
    modelTier: template.modelTier,
    prompt: template.prompt,
    enabled: true,
    activationConfiguration: cloneActivationConfiguration(
      template.activationConfiguration,
    ),
  };
}

/** Creates an empty include/exclude filter pair. */
export function emptyFileFilter(): CodeReviewFileFilterDto {
  return {
    includedFiles: [],
    excludedFiles: [],
  };
}

/** Creates an empty activation configuration. */
export function emptyActivationConfiguration(): CodeReviewerActivationConfigurationDto {
  return {
    includedFiles: [],
    excludedFiles: [],
  };
}

/** Deep-copies file match criteria arrays so form edits do not mutate store state. */
export function cloneFileFilter(
  filter: CodeReviewFileFilterDto | null,
): CodeReviewFileFilterDto {
  return {
    includedFiles: cloneCriteria(filter?.includedFiles ?? []),
    excludedFiles: cloneCriteria(filter?.excludedFiles ?? []),
  };
}

/** Deep-copies activation rules for an editable form. */
export function cloneActivationConfiguration(
  configuration: CodeReviewerActivationConfigurationDto,
): CodeReviewerActivationConfigurationDto {
  return {
    includedFiles: cloneCriteria(configuration.includedFiles),
    excludedFiles: cloneCriteria(configuration.excludedFiles),
  };
}

/** Builds the partition-aware review identity used by findings cache maps. */
export function reviewFindingsKey(review: CodeReviewRecordDto): string {
  return `${review.id}:${new Date(review.createdAtUtc).toISOString()}`;
}

function cloneCriteria(
  criteria: CodeReviewFileMatchCriteriaDto[],
): CodeReviewFileMatchCriteriaDto[] {
  return criteria.map((item) => ({
    matchType: item.matchType,
    pattern: item.pattern,
  }));
}

function agentFormToRequest(
  form: CodeReviewerAgentForm,
): CreateCodeReviewerAgentRequest & UpdateCodeReviewerAgentRequest {
  return {
    displayName: form.displayName.trim(),
    reviewFacet: form.reviewFacet.trim(),
    modelTier: form.modelTier,
    prompt: form.prompt.trim(),
    enabled: form.enabled,
    activationConfiguration: cloneActivationConfiguration(
      form.activationConfiguration,
    ),
  };
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

function sortPullRequests(
  pullRequests: CodeReviewPullRequestDto[],
): CodeReviewPullRequestDto[] {
  return [...pullRequests].sort(comparePullRequestsDescending);
}

function comparePullRequestsDescending(
  left: CodeReviewPullRequestDto,
  right: CodeReviewPullRequestDto,
): number {
  const leftCreatedAt = new Date(left.createdAtUtc).getTime();
  const rightCreatedAt = new Date(right.createdAtUtc).getTime();

  if (leftCreatedAt !== rightCreatedAt) {
    return rightCreatedAt - leftCreatedAt;
  }

  return right.id.localeCompare(left.id);
}

function isNewerThanCursor(
  pullRequest: CodeReviewPullRequestDto,
  cursor: CodeReviewStreamCursorDto,
): boolean {
  const pullRequestCreatedAt = new Date(pullRequest.createdAtUtc).getTime();
  const cursorCreatedAt = new Date(cursor.createdAtUtc).getTime();

  if (pullRequestCreatedAt !== cursorCreatedAt) {
    return pullRequestCreatedAt > cursorCreatedAt;
  }

  return pullRequest.id.localeCompare(cursor.id) > 0;
}

function isPullRequestRowUpdated(
  current: CodeReviewPullRequestDto,
  previous: CodeReviewPullRequestDto,
): boolean {
  return (
    new Date(current.updatedAtUtc).getTime() >
    new Date(previous.updatedAtUtc).getTime()
  );
}

function toNumber(value: number | string): number {
  return typeof value === "number" ? value : Number(value) || 0;
}

function errorMessage(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useCodeReviewStore, import.meta.hot));
}
