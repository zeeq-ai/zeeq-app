import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";

import { useAppStore } from "./app-store";
import type { InvitationResponse } from "@/api/generated/types/InvitationResponse";
import type { SameDomainInvitationDetailsResponse } from "@/api/generated/types/SameDomainInvitationDetailsResponse";

const apiMocks = vi.hoisted(() => ({
  acceptInvitation: vi.fn(),
  acceptInvitationAsDefault: vi.fn(),
  declineInvitation: vi.fn(),
  getMe: vi.fn(),
  getSameDomainInvitationDetails: vi.fn(),
  listInvitations: vi.fn(),
  switchOrganization: vi.fn(),
}));

vi.mock("@/api/generated", () => ({
  Auth: {
    getMe: apiMocks.getMe,
  },
  Invitations: {
    acceptInvitation: apiMocks.acceptInvitation,
    acceptInvitationAsDefault: apiMocks.acceptInvitationAsDefault,
    declineInvitation: apiMocks.declineInvitation,
    getSameDomainInvitationDetails: apiMocks.getSameDomainInvitationDetails,
    listInvitations: apiMocks.listInvitations,
  },
  Memberships: {
    switchOrganization: apiMocks.switchOrganization,
  },
}));

describe("useAppStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    sessionStorage.clear();
    vi.clearAllMocks();
  });

  it("does not reuse an authenticated-only invitation fetch for the inactive-login lookup", async () => {
    const invitation: InvitationResponse = {
      id: "inv_1",
      organizationId: "org_1",
      organizationName: "Acme",
      invitedEmail: "user@example.com",
      role: "member",
      createdAtUtc: new Date("2026-07-21T00:00:00Z"),
      expiresAtUtc: new Date("2026-07-22T00:00:00Z"),
    };
    const details: SameDomainInvitationDetailsResponse = {
      invitationId: invitation.id,
      organizationId: invitation.organizationId,
      organizationName: "Acme",
      organizationIconUrl: null,
      ownerUserId: "usr_owner",
      ownerDisplayName: "Owner",
      ownerEmail: "owner@example.com",
      ownerPictureUrl: null,
      role: invitation.role,
    };
    apiMocks.listInvitations.mockResolvedValue([invitation]);
    apiMocks.getSameDomainInvitationDetails.mockResolvedValue(details);

    const store = useAppStore();
    store.isAuthenticated = false;

    const authenticatedOnlyFetch = store.fetchSameDomainInvitation();
    const inactiveLoginFetchOptions = {
      force: false,
      allowWithoutAuthenticated: true,
    };
    const inactiveLoginFetch = store.fetchSameDomainInvitation(
      inactiveLoginFetchOptions,
    );
    await Promise.all([authenticatedOnlyFetch, inactiveLoginFetch]);

    expect(apiMocks.listInvitations).toHaveBeenCalledTimes(1);
    expect(apiMocks.getSameDomainInvitationDetails).toHaveBeenCalledWith(
      invitation.id,
    );
    expect(store.sameDomainInvitation).toEqual(details);
    expect(store.sameDomainInvitationChecked).toBe(true);
  });
});
