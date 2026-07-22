import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";

import { useSystemOrgManagementStore } from "./system-org-management-store";
import type {
  SystemOrganizationDetailsResponse,
  SystemOrganizationMemberResponse,
  SystemOrganizationSummaryResponse,
} from "@/api/generated";

const apiMocks = vi.hoisted(() => ({
  getSystemOrganization: vi.fn(),
  listSystemOrganizationMembers: vi.fn(),
  listSystemOrganizations: vi.fn(),
  updateSystemOrganization: vi.fn(),
}));

vi.mock("@/api/generated", () => ({
  SystemOrganizations: {
    getSystemOrganization: apiMocks.getSystemOrganization,
    listSystemOrganizationMembers: apiMocks.listSystemOrganizationMembers,
    listSystemOrganizations: apiMocks.listSystemOrganizations,
    updateSystemOrganization: apiMocks.updateSystemOrganization,
  },
}));

describe("useSystemOrgManagementStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
  });

  it("loads organizations with backend query state", async () => {
    const organization = organizationSummary("org_1", "Acme");
    apiMocks.listSystemOrganizations.mockResolvedValue({
      items: [organization],
      page: "2",
      pageSize: "50",
      totalCount: "1",
    });

    const store = useSystemOrgManagementStore();
    await store.loadOrganizations({ page: 2, pageSize: 50, q: "acme" });

    expect(apiMocks.listSystemOrganizations).toHaveBeenCalledWith({
      page: 2,
      pageSize: 50,
      q: "acme",
    });
    expect(store.organizations).toEqual([organization]);
    expect(store.organizationPage).toBe(2);
    expect(store.organizationPageSize).toBe(50);
    expect(store.organizationTotalCount).toBe(1);
  });

  it("ignores stale organization detail responses after selection changes", async () => {
    const first = deferred<SystemOrganizationDetailsResponse>();
    const second = deferred<SystemOrganizationDetailsResponse>();
    apiMocks.getSystemOrganization
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);

    const store = useSystemOrgManagementStore();
    const firstLoad = store.loadOrganization("org_1");
    const secondLoad = store.loadOrganization("org_2");

    first.resolve(organizationDetails("org_1", "Acme"));
    second.resolve(organizationDetails("org_2", "Beta"));
    await Promise.all([firstLoad, secondLoad]);

    expect(store.selectedOrganizationId).toBe("org_2");
    expect(store.selectedOrganization?.displayName).toBe("Beta");
  });

  it("invalidates detail responses when selection is cleared and reselected", async () => {
    const first = deferred<SystemOrganizationDetailsResponse>();
    const second = deferred<SystemOrganizationDetailsResponse>();
    apiMocks.getSystemOrganization
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);

    const store = useSystemOrgManagementStore();
    const firstLoad = store.loadOrganization("org_1");
    store.setSelectedOrganizationId(null);
    store.setSelectedOrganizationId("org_1");
    const secondLoad = store.loadOrganization("org_1");

    first.resolve(organizationDetails("org_1", "Old Acme"));
    second.resolve(organizationDetails("org_1", "Current Acme"));
    await Promise.all([firstLoad, secondLoad]);

    expect(store.selectedOrganization?.displayName).toBe("Current Acme");
  });

  it("clears previous detail and members when switching organizations", () => {
    const store = useSystemOrgManagementStore();
    store.setSelectedOrganizationId("org_1");
    store.selectedOrganization = organizationDetails("org_1", "Acme");
    store.members = [organizationMember("usr_1")];
    store.membersTotalCount = 1;

    store.setSelectedOrganizationId("org_2");

    expect(store.selectedOrganization).toBeNull();
    expect(store.members).toEqual([]);
    expect(store.membersTotalCount).toBe(0);
  });

  it("uses selection invalidation when loadOrganization switches organizations", async () => {
    const next = deferred<SystemOrganizationDetailsResponse>();
    apiMocks.getSystemOrganization.mockReturnValue(next.promise);

    const store = useSystemOrgManagementStore();
    store.setSelectedOrganizationId("org_1");
    store.selectedOrganization = organizationDetails("org_1", "Acme");
    store.members = [organizationMember("usr_1")];
    store.membersTotalCount = 1;

    const load = store.loadOrganization("org_2");

    expect(store.selectedOrganizationId).toBe("org_2");
    expect(store.selectedOrganization).toBeNull();
    expect(store.members).toEqual([]);
    expect(store.membersTotalCount).toBe(0);

    next.resolve(organizationDetails("org_2", "Beta"));
    await load;
  });

  it("patches list and selected detail state after an update", async () => {
    const original = organizationSummary("org_1", "Acme");
    const updated = organizationDetails("org_1", "Acme Enterprise");
    apiMocks.listSystemOrganizations.mockResolvedValue({
      items: [original],
      page: 1,
      pageSize: 25,
      totalCount: 1,
    });
    apiMocks.getSystemOrganization.mockResolvedValue(
      organizationDetails("org_1", "Acme"),
    );
    apiMocks.updateSystemOrganization.mockResolvedValue(updated);

    const store = useSystemOrgManagementStore();
    await store.loadOrganizations();
    await store.loadOrganization("org_1");
    await store.updateOrganization("org_1", { active: true, tier: "Team" });

    expect(apiMocks.updateSystemOrganization).toHaveBeenCalledWith("org_1", {
      active: true,
      tier: "Team",
    });
    expect(store.organizations[0].displayName).toBe("Acme Enterprise");
    expect(store.selectedOrganization?.displayName).toBe("Acme Enterprise");
  });

  it("ignores stale member responses after selection changes", async () => {
    const first = deferred<{
      items: SystemOrganizationMemberResponse[];
      page: number;
      pageSize: number;
      totalCount: number;
    }>();
    const second = deferred<{
      items: SystemOrganizationMemberResponse[];
      page: number;
      pageSize: number;
      totalCount: number;
    }>();
    apiMocks.listSystemOrganizationMembers
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);

    const store = useSystemOrgManagementStore();
    store.setSelectedOrganizationId("org_1");
    const firstLoad = store.loadMembers("org_1");
    store.setSelectedOrganizationId("org_2");
    const secondLoad = store.loadMembers("org_2");

    first.resolve({
      items: [organizationMember("usr_1")],
      page: 1,
      pageSize: 25,
      totalCount: 1,
    });
    second.resolve({
      items: [organizationMember("usr_2")],
      page: 1,
      pageSize: 25,
      totalCount: 1,
    });
    await Promise.all([firstLoad, secondLoad]);

    expect(store.members).toEqual([organizationMember("usr_2")]);
  });
});

function organizationSummary(
  id: string,
  displayName: string,
): SystemOrganizationSummaryResponse {
  return {
    id,
    displayName,
    slug: id,
    iconUrl: null,
    creator: {
      userId: "usr_creator",
      displayName: "Creator",
      email: "creator@example.com",
      pictureUrl: null,
    },
    createdAtUtc: new Date("2026-07-01T00:00:00Z"),
    updatedAtUtc: new Date("2026-07-02T00:00:00Z"),
    activatedAtUtc: new Date("2026-07-03T00:00:00Z"),
    disabledAtUtc: null,
    memberCount: 3,
    tier: "Team",
    llmConfiguration: {
      fast: llmTier("Fast"),
      high: llmTier("High"),
      max: llmTier("Max"),
    },
  };
}

function organizationDetails(
  id: string,
  displayName: string,
): SystemOrganizationDetailsResponse {
  return organizationSummary(id, displayName);
}

function organizationMember(userId: string): SystemOrganizationMemberResponse {
  return {
    userId,
    displayName: userId,
    email: `${userId}@example.com`,
    pictureUrl: null,
    role: "member",
    joinedAtUtc: new Date("2026-07-04T00:00:00Z"),
  };
}

function llmTier(tier: string) {
  return {
    tier,
    provider: "openai",
    model: "gpt-5",
    endpoint: null,
    usesManagedKey: false,
    keyId: null,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((innerResolve) => {
    resolve = innerResolve;
  });

  return { promise, resolve };
}
