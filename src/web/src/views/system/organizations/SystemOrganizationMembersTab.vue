<template>
  <UPageCard
    variant="subtle"
    class="min-w-0 overflow-hidden"
    :ui="{ container: 'p-0 sm:p-0 gap-y-0' }"
  >
    <!-- Active members are loaded server-side per organization and paged by the root view. -->
    <div class="min-w-0 overflow-x-auto">
      <UTable
        :data="members"
        :columns="columns"
        :loading="loading"
        class="min-w-160"
      >
        <template #member-cell="{ row }">
          <div class="flex min-w-0 items-center gap-3">
            <UAvatar
              :src="row.original.pictureUrl ?? undefined"
              :alt="row.original.displayName"
              icon="i-hugeicons-user"
              size="sm"
            />
            <div class="min-w-0">
              <p class="truncate text-sm font-medium text-highlighted">
                {{ row.original.displayName }}
              </p>
              <p class="truncate text-xs text-muted">
                {{ row.original.email ?? row.original.userId }}
              </p>
            </div>
          </div>
        </template>

        <template #role-cell="{ row }">
          <UBadge
            :label="row.original.role"
            color="neutral"
            variant="outline"
          />
        </template>

        <template #joined-cell="{ row }">
          {{ formatSystemOrganizationDate(row.original.joinedAtUtc) }}
        </template>

        <template #empty>
          <UEmpty icon="i-hugeicons-user-group" title="No active members" />
        </template>
      </UTable>
    </div>

    <!-- Pagination uses backend totals from the members endpoint. -->
    <div
      v-if="totalCount > 0"
      class="flex flex-col gap-3 border-t border-default p-4 sm:flex-row sm:items-center sm:justify-between"
    >
      <p class="text-sm text-muted">{{ pageRangeLabel }}</p>
      <UPagination
        :page="currentPage"
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
import type { SystemOrganizationMemberResponse } from "@/api/generated";
import {
  clampSystemOrganizationPage,
  formatSystemOrganizationDate,
} from "./organization-management";

const props = defineProps<{
  members: SystemOrganizationMemberResponse[];
  page: number;
  pageSize: number;
  totalCount: number;
  loading: boolean;
}>();

/** Emits member page changes to the root view so the store can load from the API. */
const emits = defineEmits<{
  pageChange: [page: number];
}>();

/** Column ids map to named slots for avatar, role badge, and joined-date cells. */
const columns: TableColumn<SystemOrganizationMemberResponse>[] = [
  { id: "member", header: "Member" },
  { id: "role", header: "Role" },
  { id: "joined", header: "Joined" },
];

/** Clamps stale parent pages before rendering or emitting pagination state. */
const currentPage = computed(() =>
  clampSystemOrganizationPage(props.page, props.pageSize, props.totalCount),
);

/** Displays the current one-based backend member page range. */
const pageRangeLabel = computed(() => {
  const page = currentPage.value;
  const start = (page - 1) * props.pageSize + 1;
  const end = Math.min(props.totalCount, page * props.pageSize);

  return `${start}-${end} of ${props.totalCount}`;
});

/** Emits only positive in-range page changes to the route-owning parent. */
function emitPage(page: number) {
  emits(
    "pageChange",
    clampSystemOrganizationPage(page, props.pageSize, props.totalCount),
  );
}
</script>
