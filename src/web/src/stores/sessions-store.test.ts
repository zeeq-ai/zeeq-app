import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type {
  AgentConversationListItemDto,
  AgentConversationListResponse,
  MeResponse,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { useSessionsStore } from "./sessions-store";

const apiMocks = vi.hoisted(() => ({
  listAgentConversations: vi.fn(),
}));

vi.mock("@/api/generated", () => ({
  Sessions: {
    listAgentConversations: apiMocks.listAgentConversations,
  },
}));

describe("useSessionsStore member conversations", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    useAppStore().user = testUser();
  });

  it("keeps the caller inbox request self-scoped by omitting subjectUserId", async () => {
    apiMocks.listAgentConversations.mockResolvedValue({
      items: [],
      nextCursor: null,
    });

    const store = useSessionsStore();
    await store.loadInbox();

    const params = apiMocks.listAgentConversations.mock.calls[0][1];
    expect(apiMocks.listAgentConversations).toHaveBeenCalledWith(
      "org_123",
      expect.objectContaining({ pageSize: 25 }),
    );
    expect(params).not.toHaveProperty("subjectUserId");
  });

  it("loads a member page without mutating the caller inbox", async () => {
    const inboxConversation = conversation("inbox");
    const memberConversation = conversation("member");
    apiMocks.listAgentConversations.mockResolvedValue({
      items: [memberConversation],
      nextCursor: null,
    });

    const store = useSessionsStore();
    store.conversations = [inboxConversation];
    await store.loadMemberConversations("usr_member");

    expect(apiMocks.listAgentConversations).toHaveBeenCalledWith("org_123", {
      pageSize: 100,
      subjectUserId: "usr_member",
      minimumCostUsd: 0,
    });
    expect(store.memberConversations).toEqual([memberConversation]);
    expect(store.conversations).toEqual([inboxConversation]);
  });

  it("ignores a stale response after the selected member changes", async () => {
    const first = deferred<AgentConversationListResponse>();
    const second = deferred<AgentConversationListResponse>();
    apiMocks.listAgentConversations
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);

    const store = useSessionsStore();
    const firstLoad = store.loadMemberConversations("usr_first", 5);
    const secondLoad = store.loadMemberConversations("usr_second", 10);

    second.resolve({ items: [conversation("second")], nextCursor: null });
    first.resolve({ items: [conversation("first")], nextCursor: null });
    await Promise.all([firstLoad, secondLoad]);

    expect(store.memberConversationsSubjectUserId).toBe("usr_second");
    expect(apiMocks.listAgentConversations).toHaveBeenLastCalledWith(
      "org_123",
      {
        pageSize: 100,
        subjectUserId: "usr_second",
        minimumCostUsd: 10,
      },
    );
    expect(store.memberConversations.map((item) => item.id)).toEqual([
      "second",
    ]);
    expect(store.loadingMemberConversations).toBe(false);
  });
});

function testUser(): MeResponse {
  return {
    userId: "usr_caller",
    subject: "usr_caller",
    organizationId: "org_123",
    teamId: null,
    provider: "test",
    providerSubject: "usr_caller",
    name: "Caller",
    email: "caller@example.com",
    pictureUrl: null,
    organizationRole: "member",
    organizationSlug: "test-org",
    organizations: [],
    aliases: [],
    isSystemAdmin: false,
  };
}

function conversation(id: string): AgentConversationListItemDto {
  return {
    id,
    harness: "codex",
    harnessVariant: null,
    repoRemoteUrl: null,
    headBranch: null,
    ownerEmail: "member@example.com",
    createdById: "usr_member",
    startedAtUtc: new Date("2026-08-06T12:00:00Z"),
    completedAtUtc: null,
    title: `Prompt ${id}`,
    rollupStatus: "ready",
    totalInputTokens: 100,
    totalOutputTokens: 10,
    totalCostUsd: 0.25,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((innerResolve) => {
    resolve = innerResolve;
  });

  return { promise, resolve };
}
