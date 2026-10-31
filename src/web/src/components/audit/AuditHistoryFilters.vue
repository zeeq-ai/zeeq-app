<template>
  <div class="@container border-b border-default px-5 py-3 sm:px-6">
    <div
      class="grid gap-3 @min-[32rem]:grid-cols-[minmax(0,1fr)_auto] @min-[32rem]:items-center"
    >
      <div class="flex min-w-0 items-center gap-2 text-sm">
        <span class="shrink-0 font-medium text-highlighted">
          {{ totalCount ?? entries.length }} entries{{
            totalCount === null ? " loaded" : ""
          }}
        </span>
        <UBadge
          v-if="showScopeBadge"
          :label="scopeLabel"
          color="neutral"
          variant="subtle"
          size="sm"
          class="min-w-0"
          :ui="{ label: 'truncate' }"
        />
      </div>

      <div class="flex items-center gap-2">
        <USelect
          :model-value="range"
          :items="rangeOptions"
          value-key="value"
          aria-label="Filter history by date range"
          class="min-w-0 flex-1 @min-[32rem]:w-36 @min-[32rem]:flex-none"
          @update:model-value="onRangeChange"
        />
        <UPopover
          :content="{ side: 'bottom', align: 'end', collisionPadding: 16 }"
          :ui="{
            content:
              'w-80 max-w-[calc(100vw-2rem)] max-h-[70dvh] overflow-y-auto',
          }"
        >
          <UButton
            label="Filters"
            icon="i-hugeicons-filter"
            color="neutral"
            variant="outline"
            class="shrink-0"
            :aria-label="
              activeFilters.length
                ? `Filters, ${activeFilters.length} active`
                : 'Filters'
            "
          >
            <template v-if="activeFilters.length" #trailing>
              <UBadge
                :label="activeFilters.length"
                color="neutral"
                variant="subtle"
                size="xs"
              />
            </template>
          </UButton>

          <template #content="{ close }">
            <div class="space-y-4 p-4">
              <h3 class="text-sm font-semibold text-highlighted">Filters</h3>
              <UFormField label="Type">
                <USelect
                  :model-value="typeFilter"
                  :items="typeOptions"
                  value-key="value"
                  aria-label="Filter history by change type"
                  class="w-full"
                  @update:model-value="onTypeFilterChange"
                />
              </UFormField>
              <UFormField v-if="scope !== 'document'" label="Area">
                <USelect
                  :model-value="areaFilter"
                  :items="areaOptions"
                  value-key="value"
                  aria-label="Filter history by area"
                  class="w-full"
                  @update:model-value="onAreaFilterChange"
                />
              </UFormField>
              <UFormField label="Actor" help="Actors from loaded history.">
                <USelectMenu
                  :model-value="actorFilter ?? 'all'"
                  :items="actorOptions"
                  value-key="value"
                  :search-input="{
                    placeholder: 'Search actors',
                    autofocus: false,
                  }"
                  aria-label="Filter history by actor"
                  class="w-full"
                  @update:model-value="onActorFilterChange"
                />
              </UFormField>
              <div
                class="flex items-center justify-between gap-2 border-t border-default pt-3"
              >
                <UButton
                  label="Clear filters"
                  color="neutral"
                  variant="ghost"
                  :disabled="activeFilters.length === 0"
                  @click="emits('clearFilters')"
                />
                <UButton
                  label="Done"
                  color="neutral"
                  variant="outline"
                  @click="close"
                />
              </div>
            </div>
          </template>
        </UPopover>
      </div>
    </div>

    <div
      v-if="activeFilters.length"
      class="mt-3 flex flex-wrap gap-2"
      aria-label="Active history filters"
    >
      <UButton
        v-for="filter in activeFilters"
        :key="filter.key"
        :label="`${filter.label}: ${filter.value}`"
        :title="`${filter.label}: ${filter.value}`"
        :aria-label="`Clear ${filter.label.toLowerCase()} filter: ${filter.value}`"
        trailing-icon="i-hugeicons-cancel-01"
        color="neutral"
        variant="soft"
        size="sm"
        class="max-w-full rounded-full"
        @click="clearFilter(filter.key)"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import type {
  AuditHistoryArea,
  AuditHistoryEntry,
  AuditHistoryEntryType,
  AuditHistoryRange,
  AuditHistoryScope,
} from "./audit-history-types";

const props = defineProps<{
  scope: AuditHistoryScope;
  scopeLabel: string;
  showScopeBadge: boolean;
  range: AuditHistoryRange;
  entries: AuditHistoryEntry[];
  totalCount: number | null;
  typeFilter: AuditHistoryEntryType | "all";
  areaFilter: AuditHistoryArea | "all";
  actorFilter: string | null;
  actorFilterLabel: string | null;
}>();

const emits = defineEmits<{
  typeFilterChange: [type: AuditHistoryEntryType | "all"];
  areaFilterChange: [area: AuditHistoryArea | "all"];
  actorFilterChange: [actorId: string | null, actorLabel: string | null];
  rangeChange: [range: AuditHistoryRange];
  clearFilters: [];
}>();

type FilterKey = "type" | "area" | "actor";
const activeFilters = computed(() => {
  const filters: { key: FilterKey; label: string; value: string }[] = [];
  if (props.typeFilter !== "all") {
    filters.push({
      key: "type",
      label: "Type",
      value:
        typeOptions.find((option) => option.value === props.typeFilter)
          ?.label ?? props.typeFilter,
    });
  }
  if (props.scope !== "document" && props.areaFilter !== "all") {
    filters.push({
      key: "area",
      label: "Area",
      value:
        areaOptions.find((option) => option.value === props.areaFilter)
          ?.label ?? props.areaFilter,
    });
  }
  if (props.actorFilter) {
    filters.push({
      key: "actor",
      label: "Actor",
      value: props.actorFilterLabel ?? "Selected actor",
    });
  }
  return filters;
});

function clearFilter(key: FilterKey) {
  if (key === "type") emits("typeFilterChange", "all");
  else if (key === "area") emits("areaFilterChange", "all");
  else emits("actorFilterChange", null, null);
}
const typeOptions = [
  { label: "All types", value: "all" },
  { label: "Added", value: "added" },
  { label: "Edited", value: "edited" },
  { label: "Deleted", value: "deleted" },
];
const areaOptions = [
  { label: "All areas", value: "all" },
  { label: "Content", value: "Content" },
  { label: "Review config", value: "ReviewConfiguration" },
  { label: "Settings", value: "Settings" },
  { label: "Security", value: "Security" },
];
const rangeOptions = [
  { label: "Last 90 days", value: "90-days" },
  { label: "All time", value: "all-time" },
];
const actorOptions = computed(() => {
  const actors = new Map<string, string>();
  for (const entry of props.entries) {
    if (!actors.has(entry.actorId)) actors.set(entry.actorId, entry.actorLabel);
  }
  if (props.actorFilter && !actors.has(props.actorFilter)) {
    actors.set(props.actorFilter, props.actorFilterLabel ?? "Selected actor");
  }

  return [
    { label: "All actors", value: "all" },
    ...[...actors.entries()].map(([value, label]) => ({ label, value })),
  ];
});

function onTypeFilterChange(value: string) {
  if (
    value === "all" ||
    value === "added" ||
    value === "edited" ||
    value === "deleted"
  ) {
    emits("typeFilterChange", value);
  }
}

function onAreaFilterChange(value: string) {
  if (
    value === "all" ||
    value === "Content" ||
    value === "ReviewConfiguration" ||
    value === "Settings" ||
    value === "Security"
  ) {
    emits("areaFilterChange", value);
  }
}

function onActorFilterChange(value: string) {
  if (value === "all") {
    emits("actorFilterChange", null, null);
    return;
  }
  const actor = actorOptions.value.find((option) => option.value === value);
  emits("actorFilterChange", value, actor?.label ?? null);
}

function onRangeChange(value: string) {
  if (value === "90-days" || value === "all-time") {
    emits("rangeChange", value);
  }
}
</script>
