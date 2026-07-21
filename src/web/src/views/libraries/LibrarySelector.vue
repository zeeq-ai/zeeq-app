<template>
  <!--
  Library selector: a filterable USelectMenu of library names with a "+ Add new
  library" sentinel item, including when no libraries exist yet.
  -->
  <div class="flex w-full min-w-0 items-center gap-3">
    <div v-if="activeLibrary" class="min-w-0 flex-1">
      <div class="flex items-center gap-2">
        <div class="truncate text-sm font-bold">
          {{ activeLibrary.name }}
        </div>
        <UBadge
          v-if="activeLibrary.source"
          :label="activeLibrary.source.kind"
          size="sm"
          :color="activeLibrary.source.kind === 'Public' ? 'info' : 'neutral'"
          variant="subtle"
        />
        <UBadge
          v-if="activeLibrary.source?.quarantined"
          label="Quarantined"
          size="sm"
          color="warning"
          variant="subtle"
        />
      </div>
      <div class="truncate text-xs opacity-75">
        {{ activeLibrary.description || "No description" }}
      </div>
    </div>

    <UFieldGroup>
      <UTooltip
        v-if="activeLibrary?.source"
        text="View origin repository"
        :content="{ side: 'bottom' }"
        :delay-duration="0"
      >
        <UButton
          icon="i-hugeicons-github"
          size="md"
          color="neutral"
          variant="outline"
          class="shrink-0"
          aria-label="View origin repository"
          :to="toGitHubWebUrl(activeLibrary.source.repoUrl)"
          target="_blank"
        />
      </UTooltip>

      <UButton
        v-if="showTest"
        label="Test"
        icon="i-hugeicons-search-01"
        size="md"
        color="neutral"
        variant="outline"
        class="shrink-0"
        @click="emits('test')"
      />

      <UButton
        v-if="activeLibrary"
        label="Manage"
        icon="i-hugeicons-edit-02"
        size="md"
        color="neutral"
        variant="outline"
        class="shrink-0"
        @click="emits('edit', activeLibrary)"
      />

      <UButton
        label="New"
        icon="i-hugeicons-plus-sign"
        size="md"
        color="neutral"
        variant="outline"
        class="shrink-0"
        @click="emits('add')"
      />
    </UFieldGroup>

    <USelectMenu
      :model-value="activeLibraryName ?? undefined"
      :items="selectItems"
      value-key="value"
      :loading="loading"
      :disabled="loading"
      placeholder="Select a library..."
      color="neutral"
      size="md"
      variant="outline"
      :class="['shrink-0', activeLibrary ? '' : 'ml-auto', 'font-bold', 'w-72']"
      @update:model-value="onSelect"
    />
  </div>
</template>

<script setup lang="ts">
import type { LibraryResponse } from "@/api/generated/types/LibraryResponse";
import { toGitHubWebUrl } from "@/utils/githubUrl";

const props = defineProps<{
  libraries: LibraryResponse[];
  activeLibraryName: string | null;
  loading: boolean;
  showTest: boolean;
}>();

const emits = defineEmits<{
  select: [name: string];
  add: [];
  edit: [library: LibraryResponse];
  test: [];
}>();

/** Sentinel value to distinguish the "add new" action from a library name. */
const ADD_SENTINEL = "__add__";

const activeLibrary = computed(
  () =>
    props.libraries.find(
      (library) => library.name === props.activeLibraryName,
    ) ?? null,
);

/** USelect items: library names + the add-new sentinel. */
const selectItems = computed(() => {
  const items = props.libraries.map((lib) => ({
    label: lib.name,
    value: lib.name,
  }));

  items.push({
    label: "+ Add new library",
    value: ADD_SENTINEL,
  });

  return items;
});

/** Handles selection: emits select for a library or add for the sentinel. */
function onSelect(value: string) {
  if (value === ADD_SENTINEL) {
    emits("add");
  } else {
    emits("select", value);
  }
}
</script>
