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
    <UProgress
      v-else-if="loadingOrganizations || loadingOrganization || loadingMembers"
      animation="carousel"
      size="xs"
    />
    <template v-else>
      <div class="flex items-center justify-between gap-3">
        <div class="min-w-0">
          <h2 class="text-lg font-semibold text-highlighted">Organizations</h2>
          <p class="text-sm text-muted">{{ organizationCountLabel }}</p>
        </div>
      </div>

      <div v-if="organizations.length > 0" class="divide-y divide-default">
        <div
          v-for="organization in organizations"
          :key="organization.id"
          class="grid gap-3 py-3 sm:grid-cols-[minmax(0,1fr)_auto_auto]"
        >
          <div class="min-w-0">
            <div class="truncate text-sm font-medium text-highlighted">
              {{ organization.displayName }}
            </div>
            <div class="truncate text-xs text-muted">
              {{ organization.slug ?? organization.id }}
            </div>
          </div>
          <UBadge color="neutral" variant="subtle" class="w-fit">
            {{ organization.tier }}
          </UBadge>
          <UBadge
            :color="organization.disabledAtUtc === null ? 'success' : 'error'"
            variant="subtle"
            class="w-fit"
          >
            {{ organization.disabledAtUtc === null ? "Active" : "Disabled" }}
          </UBadge>
        </div>
      </div>

      <UEmpty
        v-else
        icon="i-hugeicons-building-03"
        title="No organizations found"
      />
    </template>
  </div>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useRoute } from "vue-router";
import {
  systemOrganizationTabOptions,
  useSystemOrgManagementStore,
  type SystemOrganizationListQuery,
  type SystemOrganizationTab,
} from "@/stores/system-org-management-store";
import type { LocationQueryValue } from "vue-router";

const defaultPage = 1;
const defaultPageSize = 25;
const defaultTab: SystemOrganizationTab = "details";

const route = useRoute();
const store = useSystemOrgManagementStore();
const {
  organizations,
  organizationTotalCount,
  loadingOrganizations,
  loadingOrganization,
  loadingMembers,
  error,
} = storeToRefs(store);

const organizationCountLabel = computed(() => {
  const count = organizationTotalCount.value;

  return count === 1 ? "1 organization" : `${count} organizations`;
});

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

function readRouteState() {
  return {
    listQuery: {
      page: readPositiveInt(route.query.page, defaultPage),
      pageSize: readPositiveInt(route.query.pageSize, defaultPageSize),
      q: readString(route.query.q),
    } satisfies SystemOrganizationListQuery,
    orgId: readNullableString(route.query.org),
    tab: readTab(route.query.tab),
  };
}

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

function readString(value: LocationQueryValue | LocationQueryValue[]) {
  return readNullableString(value) ?? "";
}

function readNullableString(value: LocationQueryValue | LocationQueryValue[]) {
  if (Array.isArray(value)) {
    return value[0] ?? null;
  }

  return value;
}

function readTab(value: LocationQueryValue | LocationQueryValue[]) {
  const tab = readNullableString(value);

  return isSystemOrganizationTab(tab) ? tab : defaultTab;
}

function isSystemOrganizationTab(
  value: string | null,
): value is SystemOrganizationTab {
  return systemOrganizationTabOptions.some((tab) => tab === value);
}
</script>
