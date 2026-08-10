import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Sessions,
  type AgentConversationListItemDto,
  type AgentConversationDetailResponse,
  type AgentConversationStreamCursorDto,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";

/**
 * Store for the Sessions product area: the agent-conversation inbox and one
 * selected conversation's detail (prompt timeline + token usage).
 *
 * Shaped like `code-review-store.ts`'s inbox slice — cursor-paginated list and
 * a router-synced selection — since the Sessions inbox mirrors the PR Code
 * Reviews inbox layout. The inbox remains the caller's own conversations; the
 * separate member-conversation slice backs the Home usage drill-down without
 * mutating inbox pagination or selection state.
 */
export const useSessionsStore = defineStore("sessions-store", () => {
  const appStore = useAppStore();

  const activeOrganizationId = computed(
    () =>
      appStore.currentOrganization?.id ?? appStore.user?.organizationId ?? "",
  );

  const conversations = ref<AgentConversationListItemDto[]>([]);
  const nextCursor = ref<AgentConversationStreamCursorDto | null>(null);
  const loadingInbox = ref(false);
  const loadingMore = ref(false);
  const selectedConversation = ref<AgentConversationListItemDto | null>(null);
  const selectedConversationDetail =
    ref<AgentConversationDetailResponse | null>(null);
  const loadingDetail = ref(false);
  const error = ref<string | null>(null);

  const memberConversations = ref<AgentConversationListItemDto[]>([]);
  const memberConversationsSubjectUserId = ref<string | null>(null);
  const loadingMemberConversations = ref(false);
  const memberConversationsError = ref<string | null>(null);

  let memberConversationsRequestId = 0;

  // "Me" tab's always-visible recent-sessions preview. Deliberately separate from
  // memberConversations (which is owned by the cost-filtered MemberSessionsSlideover) so the
  // slideover's minimum-cost slider never clobbers this unfiltered preview list.
  const mySessions = ref<AgentConversationListItemDto[]>([]);
  const loadingMySessions = ref(false);
  const mySessionsError = ref<string | null>(null);

  /** Loads the first inbox page for the active organization. */
  async function loadInbox() {
    await loadConversations({ reset: true });
  }

  /**
   * Loads the next cursor page, appending to the existing rows. Guards against
   * concurrent calls (e.g. a double-click before the first request settles) reusing the
   * same `nextCursor` and appending a duplicate page.
   */
  async function loadNextPage() {
    if (loadingMore.value || !nextCursor.value) {
      return;
    }

    await loadConversations({ reset: false });
  }

  /** Shared load path for both the first page (reset) and cursor pagination (append). */
  async function loadConversations(options: { reset: boolean }) {
    const orgId = requireOrganizationId();
    const loadingRef = options.reset ? loadingInbox : loadingMore;
    loadingRef.value = true;
    error.value = null;

    try {
      const cursor = options.reset ? null : nextCursor.value;
      const response = await Sessions.listAgentConversations(orgId, {
        cursorStartedAtUtc: cursor?.startedAtUtc,
        cursorId: cursor?.id,
        pageSize: 25,
      });

      conversations.value = options.reset
        ? response.items
        : [...conversations.value, ...response.items];
      nextCursor.value = response.nextCursor;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load conversations.");
      throw err;
    } finally {
      loadingRef.value = false;
    }
  }

  /**
   * Loads the newest dashboard conversations for one active organization member.
   * The request id prevents a slower response for a previously selected member from
   * overwriting the current slideover after rapid listbox navigation.
   */
  async function loadMemberConversations(
    subjectUserId: string,
    minimumCostUsd = 0,
  ) {
    const orgId = requireOrganizationId();
    const requestId = ++memberConversationsRequestId;

    memberConversationsSubjectUserId.value = subjectUserId;
    memberConversations.value = [];
    loadingMemberConversations.value = true;
    memberConversationsError.value = null;

    try {
      const response = await Sessions.listAgentConversations(orgId, {
        pageSize: 100,
        subjectUserId,
        minimumCostUsd,
      });

      if (requestId !== memberConversationsRequestId) {
        return;
      }

      memberConversations.value = response.items;
    } catch (err: unknown) {
      if (requestId !== memberConversationsRequestId) {
        return;
      }

      memberConversationsError.value = errorMessage(
        err,
        "Could not load member conversations.",
      );
      throw err;
    } finally {
      if (requestId === memberConversationsRequestId) {
        loadingMemberConversations.value = false;
      }
    }
  }

  /**
   * Loads the signed-in user's most recent conversations for the "Me" tab preview (no cost
   * filter — "most recent", not "most expensive"). Skips the request when the identity has no
   * userId on record, leaving the panel empty rather than sending a request with an empty
   * subjectUserId (which the API would not scope to anyone in particular).
   */
  async function loadMySessions(limit = 25) {
    const userId = appStore.user?.userId;
    if (!userId) {
      mySessions.value = [];
      return;
    }

    const orgId = requireOrganizationId();
    loadingMySessions.value = true;
    mySessionsError.value = null;

    try {
      // minimumCostUsd must be passed explicitly: omitting it entirely means "inbox
      // behavior" (no cost floor at all, per the endpoint's own doc comment), while an
      // explicit 0 applies the intended $0.10 known-cost default -- the same default
      // loadMemberConversations already applies via its `minimumCostUsd = 0` parameter.
      const response = await Sessions.listAgentConversations(orgId, {
        pageSize: limit,
        subjectUserId: userId,
        minimumCostUsd: 0,
      });
      mySessions.value = response.items;
    } catch (err: unknown) {
      mySessionsError.value = errorMessage(
        err,
        "Could not load recent sessions.",
      );
      throw err;
    } finally {
      loadingMySessions.value = false;
    }
  }

  /** Clears dashboard-only conversation state and invalidates any request in flight. */
  function clearMemberConversations() {
    memberConversationsRequestId += 1;
    memberConversations.value = [];
    memberConversationsSubjectUserId.value = null;
    loadingMemberConversations.value = false;
    memberConversationsError.value = null;
  }

  /** Loads one conversation's detail and marks it selected. */
  async function selectConversation(
    conversation: AgentConversationListItemDto,
  ) {
    selectedConversation.value = conversation;
    await loadConversationById(conversation.id);
  }

  /**
   * Direct-load path for deep links: loads a conversation's detail by id
   * regardless of whether it is present in the currently loaded inbox page.
   */
  async function loadConversationById(conversationId: string) {
    const orgId = requireOrganizationId();
    loadingDetail.value = true;
    error.value = null;

    try {
      const detail = await Sessions.getAgentConversationDetail(
        orgId,
        conversationId,
      );
      selectedConversationDetail.value = detail;
      selectedConversation.value = detail.summary;
    } catch (err: unknown) {
      error.value = errorMessage(err, "Could not load conversation.");
      throw err;
    } finally {
      loadingDetail.value = false;
    }
  }

  /** Clears the selected conversation and its loaded detail. */
  function clearSelection() {
    selectedConversation.value = null;
    selectedConversationDetail.value = null;
  }

  function requireOrganizationId(): string {
    if (!activeOrganizationId.value) {
      throw new Error("Select an organization before using Sessions.");
    }

    return activeOrganizationId.value;
  }

  return {
    activeOrganizationId,
    conversations,
    nextCursor,
    loadingInbox,
    loadingMore,
    selectedConversation,
    selectedConversationDetail,
    loadingDetail,
    error,
    memberConversations,
    memberConversationsSubjectUserId,
    loadingMemberConversations,
    memberConversationsError,
    mySessions,
    loadingMySessions,
    mySessionsError,
    loadInbox,
    loadNextPage,
    loadMemberConversations,
    loadMySessions,
    selectConversation,
    loadConversationById,
    clearSelection,
    clearMemberConversations,
  };
});

function errorMessage(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useSessionsStore, import.meta.hot));
}
