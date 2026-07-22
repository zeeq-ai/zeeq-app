<template>
  <UPageCard
    variant="subtle"
    class="min-w-0 overflow-hidden"
    :ui="{ container: 'p-0 sm:p-0 gap-y-0' }"
  >
    <!-- Table toolbar controls backend search and pagination; no client-side Fuse matching here. -->
    <div
      class="flex flex-col gap-3 border-b border-default p-4 lg:flex-row lg:items-center lg:justify-between"
    >
      <div class="min-w-0">
        <h2 class="text-base font-semibold text-highlighted">Organizations</h2>
        <p class="mt-1 text-sm text-muted">{{ organizationCountLabel }}</p>
      </div>

      <div class="flex flex-col gap-2 sm:flex-row sm:items-center lg:w-auto">
        <UInput
          :model-value="query"
          icon="i-hugeicons-search-01"
          placeholder="Search organizations"
          aria-label="Search organizations"
          class="min-w-0 sm:w-72"
          @update:model-value="emitSearch"
        >
          <template #trailing>
            <UButton
              v-if="query"
              icon="i-hugeicons-cancel-01"
              size="xs"
              color="neutral"
              variant="ghost"
              aria-label="Clear search"
              @click="emits('search', '')"
            />
          </template>
        </UInput>

        <USelect
          :model-value="pageSize"
          :items="pageSizeItems"
          color="neutral"
          aria-label="Rows per page"
          class="w-full sm:w-32"
          @update:model-value="emitPageSize"
        />

        <UTooltip text="Refresh organizations">
          <UButton
            icon="i-hugeicons-refresh"
            color="neutral"
            variant="ghost"
            aria-label="Refresh organizations"
            :loading="loading"
            @click="emits('refresh')"
          />
        </UTooltip>
      </div>
    </div>

    <!-- Dense server-backed organization table. Row actions emit selection to the route root. -->
    <div class="min-w-0 overflow-x-auto">
      <UTable
        :data="organizations"
        :columns="columns"
        :loading="loading"
        class="min-w-225"
      >
        <template #organization-cell="{ row }">
          <div class="flex min-w-0 items-center gap-3">
            <UAvatar
              :src="row.original.iconUrl ?? undefined"
              :alt="row.original.displayName"
              icon="i-hugeicons-building-03"
              size="sm"
            />
            <div class="min-w-0">
              <button
                class="block max-w-full truncate text-left text-sm font-medium text-highlighted hover:underline"
                :class="{
                  'text-primary': selectedOrganizationId === row.original.id,
                }"
                type="button"
                @click="emits('select', row.original.id)"
              >
                {{ row.original.displayName }}
              </button>
              <p class="truncate text-xs text-muted">
                {{ row.original.slug ?? row.original.id }}
              </p>
            </div>
          </div>
        </template>

        <template #creator-cell="{ row }">
          <div class="min-w-0 text-sm">
            <div class="truncate text-default">
              {{ row.original.creator.displayName }}
            </div>
            <div class="truncate text-xs text-muted">
              {{ row.original.creator.email ?? row.original.creator.userId }}
            </div>
          </div>
        </template>

        <template #status-cell="{ row }">
          <UBadge
            :label="
              isSystemOrganizationActive(row.original) ? 'Active' : 'Inactive'
            "
            :color="
              isSystemOrganizationActive(row.original) ? 'success' : 'neutral'
            "
            variant="subtle"
          />
        </template>

        <template #tier-cell="{ row }">
          <UBadge
            :label="row.original.tier"
            color="neutral"
            variant="outline"
          />
        </template>

        <template #members-cell="{ row }">
          {{ row.original.memberCount }}
        </template>

        <template #dates-cell="{ row }">
          {{ formatSystemOrganizationDate(row.original.createdAtUtc) }}
        </template>

        <template #actions-cell="{ row }">
          <UButton
            label="View"
            icon="i-hugeicons-view"
            color="neutral"
            variant="ghost"
            size="xs"
            @click="emits('select', row.original.id)"
          />
        </template>

        <template #empty>
          <UEmpty
            icon="i-hugeicons-building-03"
            title="No organizations found"
          />
        </template>
      </UTable>
    </div>

    <!-- Pagination reflects backend totals, not local table row counts. -->
    <div
      v-if="totalCount > 0"
      class="flex flex-col gap-3 border-t border-default p-4 sm:flex-row sm:items-center sm:justify-between"
    >
      <p class="text-sm text-muted">{{ pageRangeLabel }}</p>
      <UPagination
        :page
        :items-per-page="pageSize"
        :total="totalCount"
        variant="soft"
        active-variant="soft"
        @update:page="emitPage"
      />
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import type { TableColumn } from "@nuxt/ui";
import type { SystemOrganizationSummaryResponse } from "@/api/generated";
import {
  clampSystemOrganizationPage,
  formatSystemOrganizationDate,
  isSystemOrganizationActive,
  systemOrganizationPageSizeOptions,
  toSystemOrganizationPageSize,
} from "./organization-management";

const props = defineProps<{
  organizations: SystemOrganizationSummaryResponse[];
  loading: boolean;
  page: number;
  pageSize: number;
  totalCount: number;
  query: string;
  selectedOrganizationId: string | null;
}>();

/** Emits route-level list and selection intents to the root view. */
const emits = defineEmits<{
  refresh: [];
  search: [query: string];
  pageChange: [page: number];
  pageSizeChange: [pageSize: number];
  select: [orgId: string];
}>();

/** Column ids map to named slots because several cells render compound data. */
const columns: TableColumn<SystemOrganizationSummaryResponse>[] = [
  { id: "organization", header: "Organization" },
  { id: "creator", header: "Creator" },
  { id: "status", header: "Status" },
  { id: "tier", header: "Tier" },
  { id: "members", header: "Members" },
  { id: "dates", header: "Created" },
  { id: "actions", header: "" },
];
/** Server page-size options accepted by the backend API bounds. */
const pageSizeItems: { label: string; value: number }[] =
  systemOrganizationPageSizeOptions.map((value) => ({
    label: `${value} rows`,
    value,
  }));

/** Displays the backend total count for the current query. */
const organizationCountLabel = computed(() =>
  props.totalCount === 1
    ? "1 organization"
    : `${props.totalCount} organizations`,
);
/** Displays the current one-based backend page range. */
const pageRangeLabel = computed(() => {
  if (props.totalCount === 0) {
    return "0 of 0";
  }

  const page = clampSystemOrganizationPage(
    props.page,
    props.pageSize,
    props.totalCount,
  );
  const start = (page - 1) * props.pageSize + 1;
  const end = Math.min(props.totalCount, page * props.pageSize);

  return `${start}-${end} of ${props.totalCount}`;
});

/** Normalizes Nuxt UI input update values before emitting query text. */
function emitSearch(value: string | number) {
  emits("search", String(value).trim());
}

/** Emits only page sizes accepted by the backend route contract. */
function emitPageSize(value: string | number) {
  const pageSize = toSystemOrganizationPageSize(value);
  if (pageSize !== null) {
    emits("pageSizeChange", pageSize);
  }
}

/** Emits only positive in-range page changes from the pagination control. */
function emitPage(page: number) {
  emits(
    "pageChange",
    clampSystemOrganizationPage(page, props.pageSize, props.totalCount),
  );
}
</script>
