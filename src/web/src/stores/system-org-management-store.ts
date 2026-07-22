import { defineStore, acceptHMRUpdate } from "pinia";
import {
  SystemOrganizations,
  type SystemOrganizationDetailsResponse,
  type SystemOrganizationMemberResponse,
  type SystemOrganizationSummaryResponse,
  type UpdateSystemOrganizationRequest,
} from "@/api/generated";

export const systemOrganizationTabOptions = [
  "details",
  "members",
  "llm",
] as const;

export type SystemOrganizationTab =
  (typeof systemOrganizationTabOptions)[number];

export type SystemOrganizationListQuery = {
  page: number;
  pageSize: number;
  q: string;
};

const defaultListQuery: SystemOrganizationListQuery = {
  page: 1,
  pageSize: 25,
  q: "",
};

const defaultMembersPage = 1;
const defaultMembersPageSize = 25;

/**
 * Store for system-admin organization management.
 *
 * The list, detail, and members loaders each carry independent request tokens
 * so slower responses from an older route state cannot overwrite the current
 * selected organization or visible page.
 */
export const useSystemOrgManagementStore = defineStore(
  "system-org-management-store",
  () => {
    const organizations = ref<SystemOrganizationSummaryResponse[]>([]);
    const organizationTotalCount = ref(0);
    const organizationPage = ref(defaultListQuery.page);
    const organizationPageSize = ref(defaultListQuery.pageSize);
    const organizationSearchQuery = ref(defaultListQuery.q);

    const selectedOrganizationId = ref<string | null>(null);
    const selectedOrganization = ref<SystemOrganizationDetailsResponse | null>(
      null,
    );
    const selectedTab = ref<SystemOrganizationTab>("details");

    const members = ref<SystemOrganizationMemberResponse[]>([]);
    const membersTotalCount = ref(0);
    const membersPage = ref(defaultMembersPage);
    const membersPageSize = ref(defaultMembersPageSize);

    const loadingOrganizations = ref(false);
    const loadingOrganization = ref(false);
    const loadingMembers = ref(false);
    const savingOrganization = ref(false);
    const error = ref<string | null>(null);

    let organizationsRequestId = 0;
    let organizationRequestId = 0;
    let membersRequestId = 0;

    const hasSelectedOrganization = computed(
      () => selectedOrganizationId.value !== null,
    );

    /** Applies route-backed list query state without issuing network requests. */
    function setListQuery(query: Partial<SystemOrganizationListQuery>) {
      organizationPage.value = query.page ?? organizationPage.value;
      organizationPageSize.value = query.pageSize ?? organizationPageSize.value;
      organizationSearchQuery.value = query.q ?? organizationSearchQuery.value;
    }

    /** Applies route-backed selected organization state. */
    function setSelectedOrganizationId(orgId: string | null) {
      if (selectedOrganizationId.value !== orgId) {
        organizationRequestId++;
        membersRequestId++;
        loadingOrganization.value = false;
        loadingMembers.value = false;
        selectedOrganization.value = null;
        members.value = [];
        membersTotalCount.value = 0;
      }

      selectedOrganizationId.value = orgId;
    }

    /** Applies route-backed slideover tab state. */
    function setSelectedTab(tab: SystemOrganizationTab) {
      selectedTab.value = tab;
    }

    /** Loads the current organization list page from the backend. */
    async function loadOrganizations(
      query: Partial<SystemOrganizationListQuery> = {},
    ) {
      setListQuery(query);

      const requestId = ++organizationsRequestId;
      loadingOrganizations.value = true;
      error.value = null;

      try {
        const response = await SystemOrganizations.listSystemOrganizations({
          page: organizationPage.value,
          pageSize: organizationPageSize.value,
          q: organizationSearchQuery.value || undefined,
        });

        if (requestId !== organizationsRequestId) {
          return response;
        }

        organizations.value = response.items;
        organizationPage.value = toNumber(response.page);
        organizationPageSize.value = toNumber(response.pageSize);
        organizationTotalCount.value = toNumber(response.totalCount);

        return response;
      } catch (err: unknown) {
        if (requestId === organizationsRequestId) {
          error.value = toErrorMessage(err);
        }
        throw err;
      } finally {
        if (requestId === organizationsRequestId) {
          loadingOrganizations.value = false;
        }
      }
    }

    /** Loads detail state for the selected organization. */
    async function loadOrganization(orgId = selectedOrganizationId.value) {
      if (orgId === null) {
        setSelectedOrganizationId(null);
        return null;
      }

      setSelectedOrganizationId(orgId);

      const requestId = ++organizationRequestId;
      loadingOrganization.value = true;
      error.value = null;

      try {
        const organization =
          await SystemOrganizations.getSystemOrganization(orgId);

        if (
          requestId !== organizationRequestId ||
          selectedOrganizationId.value !== orgId
        ) {
          return organization;
        }

        selectedOrganization.value = organization;
        patchOrganization(organization);

        return organization;
      } catch (err: unknown) {
        if (requestId === organizationRequestId) {
          error.value = toErrorMessage(err);
        }
        throw err;
      } finally {
        if (requestId === organizationRequestId) {
          loadingOrganization.value = false;
        }
      }
    }

    /** Loads active members for the selected organization. */
    async function loadMembers(
      orgId = selectedOrganizationId.value,
      page = membersPage.value,
      pageSize = membersPageSize.value,
    ) {
      if (orgId === null) {
        members.value = [];
        membersTotalCount.value = 0;
        return null;
      }

      membersPage.value = page;
      membersPageSize.value = pageSize;

      const requestId = ++membersRequestId;
      loadingMembers.value = true;
      error.value = null;

      try {
        const response =
          await SystemOrganizations.listSystemOrganizationMembers(orgId, {
            page,
            pageSize,
          });

        if (
          requestId !== membersRequestId ||
          selectedOrganizationId.value !== orgId
        ) {
          return response;
        }

        members.value = response.items;
        membersPage.value = toNumber(response.page);
        membersPageSize.value = toNumber(response.pageSize);
        membersTotalCount.value = toNumber(response.totalCount);

        return response;
      } catch (err: unknown) {
        if (requestId === membersRequestId) {
          error.value = toErrorMessage(err);
        }
        throw err;
      } finally {
        if (requestId === membersRequestId) {
          loadingMembers.value = false;
        }
      }
    }

    /** Sends the backend PATCH and updates visible rows/details from the response. */
    async function updateOrganization(
      orgId: string,
      request: UpdateSystemOrganizationRequest,
    ) {
      savingOrganization.value = true;
      error.value = null;

      try {
        const organization = await SystemOrganizations.updateSystemOrganization(
          orgId,
          request,
        );

        patchOrganization(organization);

        if (selectedOrganizationId.value === orgId) {
          selectedOrganization.value = organization;
        }

        return organization;
      } catch (err: unknown) {
        error.value = toErrorMessage(err);
        throw err;
      } finally {
        savingOrganization.value = false;
      }
    }

    /** Patches the current list row and detail panel with a fresh organization response. */
    function patchOrganization(
      organization:
        | SystemOrganizationDetailsResponse
        | SystemOrganizationSummaryResponse,
    ) {
      const index = organizations.value.findIndex(
        (item) => item.id === organization.id,
      );

      if (index >= 0) {
        organizations.value[index] = organization;
      }

      if (selectedOrganization.value?.id === organization.id) {
        selectedOrganization.value = organization;
      }
    }

    return {
      organizations,
      organizationTotalCount,
      organizationPage,
      organizationPageSize,
      organizationSearchQuery,
      selectedOrganizationId,
      selectedOrganization,
      selectedTab,
      members,
      membersTotalCount,
      membersPage,
      membersPageSize,
      loadingOrganizations,
      loadingOrganization,
      loadingMembers,
      savingOrganization,
      error,
      hasSelectedOrganization,
      setListQuery,
      setSelectedOrganizationId,
      setSelectedTab,
      loadOrganizations,
      loadOrganization,
      loadMembers,
      updateOrganization,
      patchOrganization,
    };
  },
);

/** Normalizes generated number|string fields for numeric UI controls. */
function toNumber(value: number | string) {
  return typeof value === "number" ? value : Number(value);
}

/** Normalizes thrown values for the system organization admin surface. */
function toErrorMessage(err: unknown): string {
  return err instanceof Error
    ? err.message
    : "System organization request failed";
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useSystemOrgManagementStore, import.meta.hot),
  );
}
