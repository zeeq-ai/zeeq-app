<template>
  <div class="flex min-h-0 flex-1 flex-col gap-4">
    <UAlert
      v-if="error"
      title="Organizations unavailable"
      :description="error"
      icon="i-hugeicons-alert-02"
      color="error"
      variant="subtle"
    />

    <!-- Organization table owns no store state; all route-backed list changes emit upward. -->
    <SystemOrganizationsTable
      :organizations
      :loading="loadingOrganizations"
      :page="organizationPage"
      :page-size="organizationPageSize"
      :total-count="organizationTotalCount"
      :query="organizationSearchQuery"
      :selected-organization-id
      @refresh="refreshOrganizations"
      @search="updateSearch"
      @page-change="updatePage"
      @page-size-change="updatePageSize"
      @select="selectOrganization"
    />

    <!-- Detail slideover waits for loaded data so route ids cannot open an empty shell. -->
    <SystemOrganizationSlideover
      v-if="selectedOrganization !== null"
      :open="selectedOrganization !== null"
      :organization="selectedOrganization"
      :members
      :members-page
      :members-page-size
      :members-total-count
      :loading="loadingOrganization"
      :loading-members
      :saving="savingOrganization"
      :active-tab="selectedTab"
      @close="closeOrganization"
      @tab-change="updateTab"
      @save="saveOrganization"
      @members-page-change="loadMembersPage"
    />
  </div>
</template>

<script setup lang="ts">
import { useDebounceFn } from "@vueuse/core";
import { storeToRefs } from "pinia";
import { useRoute, useRouter } from "vue-router";
import {
  systemOrganizationTabOptions,
  useSystemOrgManagementStore,
  type SystemOrganizationListQuery,
  type SystemOrganizationTab,
  type SystemOrganizationTier,
} from "@/stores/system-org-management-store";
import type { LocationQueryRaw, LocationQueryValue } from "vue-router";
import SystemOrganizationsTable from "./SystemOrganizationsTable.vue";
import SystemOrganizationSlideover from "./SystemOrganizationSlideover.vue";
import {
  clampSystemOrganizationPage,
  toSystemOrganizationPageSize,
} from "./organization-management";

const defaultPage = 1;
const defaultPageSize = 25;
const defaultTab: SystemOrganizationTab = "details";

const route = useRoute();
const router = useRouter();
const toast = useToast();
const store = useSystemOrgManagementStore();
const {
  organizations,
  organizationTotalCount,
  organizationPage,
  organizationPageSize,
  organizationSearchQuery,
  selectedOrganizationId,
  selectedOrganization,
  selectedTab,
  members,
  membersPage,
  membersPageSize,
  membersTotalCount,
  loadingOrganizations,
  loadingOrganization,
  loadingMembers,
  savingOrganization,
  error,
} = storeToRefs(store);

/**
 * Route query is the source of truth for list filters and selected details.
 *
 * The root view is the only component that touches Pinia for this feature; child
 * components emit intent and receive store state as props.
 */
watch(
  () => route.query,
  () => {
    const routeState = readRouteState();

    store.setListQuery(routeState.listQuery);
    store.setSelectedOrganizationId(routeState.orgId);
    store.setSelectedTab(routeState.tab);

    void loadRouteState(routeState.orgId).catch(() => {
      // The store has already populated `error`; this prevents an unhandled rejection.
    });
  },
  { immediate: true },
);

/** Reloads the current server-side list page without changing route state. */
async function refreshOrganizations() {
  try {
    await store.loadOrganizations();
  } catch (err: unknown) {
    showError("Could not refresh organizations.", err);
  }
}

/** Debounces backend search state so normal typing does not issue one request per key. */
const updateSearch = useDebounceFn(async (query: string) => {
  await replaceQuery({
    page: undefined,
    q: query.trim() || undefined,
  });
}, 250);

/** Writes one-based server pagination state into the URL. */
async function updatePage(page: number) {
  const nextPage = clampSystemOrganizationPage(
    page,
    organizationPageSize.value,
    organizationTotalCount.value,
  );

  await replaceQuery({
    page: nextPage === defaultPage ? undefined : String(nextPage),
  });
}

/** Writes the requested server page size into the URL and returns to page one. */
async function updatePageSize(pageSize: number) {
  const nextPageSize = toSystemOrganizationPageSize(pageSize);
  if (nextPageSize === null) {
    return;
  }

  await replaceQuery({
    page: undefined,
    pageSize:
      nextPageSize === defaultPageSize ? undefined : String(nextPageSize),
  });
}

/** Opens the slideover for an organization without discarding the current tab. */
async function selectOrganization(orgId: string) {
  await replaceQuery({ org: orgId, tab: route.query.tab });
}

/** Closes the slideover and removes detail-only query parameters. */
async function closeOrganization() {
  await replaceQuery({ org: undefined, tab: undefined });
}

/** Persists the active slideover tab in the URL for shareable detail links. */
async function updateTab(tab: SystemOrganizationTab) {
  await replaceQuery({
    tab: tab === defaultTab ? undefined : tab,
  });
}

/** Saves activation/tier edits and lets the store patch visible row/detail state. */
async function saveOrganization(request: {
  active: boolean;
  tier: SystemOrganizationTier;
}) {
  const organization = selectedOrganization.value;
  if (!organization) {
    return;
  }

  try {
    await store.updateOrganization(organization.id, request);
    toast.add({
      title: "Organization updated",
      icon: "i-hugeicons-tick-02",
      color: "success",
    });
  } catch (err: unknown) {
    showError("Could not update organization.", err);
  }
}

/** Loads one server-side page of active members for the selected organization. */
async function loadMembersPage(page: number) {
  try {
    await store.loadMembers(selectedOrganizationId.value, page);
  } catch (err: unknown) {
    showError("Could not load members.", err);
  }
}

/** Parses URL query into typed store state, applying defaults for omitted values. */
function readRouteState() {
  return {
    listQuery: {
      page: readPositiveInt(route.query.page, defaultPage),
      pageSize:
        toSystemOrganizationPageSize(
          readNullableString(route.query.pageSize),
        ) ?? defaultPageSize,
      q: readString(route.query.q),
    } satisfies SystemOrganizationListQuery,
    orgId: readNullableString(route.query.org),
    tab: readTab(route.query.tab),
  };
}

/** Loads list state and selected detail state for the current route. */
async function loadRouteState(orgId: string | null) {
  const organizationsLoad = store.loadOrganizations();

  if (orgId === null) {
    await organizationsLoad;
    return;
  }

  await Promise.all([
    organizationsLoad,
    store.loadOrganization(orgId),
    store.loadMembers(orgId),
  ]);
}

/** Applies query updates while dropping empty/default removal sentinels. */
async function replaceQuery(changes: Record<string, LocationQueryRaw[string]>) {
  await router.replace({ query: cleanQuery({ ...route.query, ...changes }) });
}

/** Normalizes Vue Router query objects after assigning `undefined` to remove keys. */
function cleanQuery(query: Record<string, unknown>): LocationQueryRaw {
  const clean: LocationQueryRaw = {};

  for (const [key, value] of Object.entries(query)) {
    if (Array.isArray(value)) {
      const values = value.filter(
        (item) =>
          (typeof item === "string" && item !== "") || typeof item === "number",
      );
      if (values.length > 0) {
        clean[key] = values;
      }
    } else if (
      (typeof value === "string" && value !== "") ||
      typeof value === "number"
    ) {
      clean[key] = value;
    }
  }

  return clean;
}

/** Reads positive integer query values used by backend pagination. */
function readPositiveInt(
  value: LocationQueryValue | LocationQueryValue[],
  fallback: number,
) {
  const rawValue = readNullableString(value);
  const numericValue = rawValue === null ? Number.NaN : Number(rawValue);

  return Number.isInteger(numericValue) && numericValue > 0
    ? numericValue
    : fallback;
}

/** Reads a string query value, treating missing values as an empty string. */
function readString(value: LocationQueryValue | LocationQueryValue[]) {
  return readNullableString(value) ?? "";
}

/** Reads the first value for query parameters where repeated keys are not meaningful. */
function readNullableString(value: LocationQueryValue | LocationQueryValue[]) {
  if (Array.isArray(value)) {
    return value[0] ?? null;
  }

  return value ?? null;
}

/** Reads a supported slideover tab, defaulting to the general details tab. */
function readTab(value: LocationQueryValue | LocationQueryValue[]) {
  const tab = readNullableString(value);

  return isSystemOrganizationTab(tab) ? tab : defaultTab;
}

/** Narrows arbitrary query strings to the supported system organization tabs. */
function isSystemOrganizationTab(
  value: string | null,
): value is SystemOrganizationTab {
  return systemOrganizationTabOptions.some((tab) => tab === value);
}

/** Shows root-level action failures in the app toast surface. */
function showError(title: string, err: unknown) {
  toast.add({
    title,
    description: err instanceof Error ? err.message : undefined,
    icon: "i-hugeicons-alert-02",
    color: "error",
  });
}
</script>
