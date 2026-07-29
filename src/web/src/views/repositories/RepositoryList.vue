<template>
  <!--
  Inbox-shaped repository picker for the Repositories view. Mirrors the agent
  list in Manage Agents: dense rows, left accent border marking the selection,
  detail lives in the panel beside it.
  -->
  <section
    class="flex min-h-0 basis-full flex-col border-r border-default lg:max-w-[26rem] lg:basis-[26rem]"
  >
    <div
      class="flex min-h-[67px] items-center justify-between gap-3 border-b border-default px-4 py-3 sm:px-6"
    >
      <div class="flex min-w-0 items-center gap-2">
        <h2 class="text-base font-semibold text-highlighted">Repositories</h2>
        <UBadge :label="rows.length" color="primary" variant="subtle" />
      </div>

      <div class="flex shrink-0 items-center gap-2">
        <UTabs
          v-model="scope"
          :items="scopeItems"
          :content="false"
          color="neutral"
          variant="pill"
          size="xs"
          class="shrink-0"
          :ui="compactTabsUi"
        />

        <UButton
          label="Admin"
          icon="i-hugeicons-settings-01"
          color="neutral"
          variant="subtle"
          size="sm"
          to="/settings/github"
        />
      </div>
    </div>

    <div v-if="loading" class="grid gap-2 p-4 sm:px-6">
      <USkeleton v-for="index in 4" :key="index" class="h-16 rounded-md" />
    </div>

    <UEmpty
      v-else-if="repositories.length === 0"
      icon="i-hugeicons-github"
      title="No repositories configured"
      description="An organization owner or admin must enable a repository in GitHub settings before it can be configured here."
      class="flex-1"
    >
      <template #actions>
        <UButton
          label="Open GitHub settings"
          icon="i-hugeicons-settings-01"
          color="neutral"
          variant="subtle"
          to="/settings/github"
        />
      </template>
    </UEmpty>

    <UEmpty
      v-else-if="rows.length === 0"
      icon="i-hugeicons-github"
      title="No enabled repositories"
      description="Every configured repository is paused. Switch to All to configure a paused repository."
      class="flex-1"
    >
      <template #actions>
        <UButton
          label="Show all"
          color="neutral"
          variant="subtle"
          @click="scope = 'all'"
        />
      </template>
    </UEmpty>

    <div v-else class="min-h-0 flex-1 overflow-y-auto">
      <button
        v-for="repository in rows"
        :key="repository.id"
        type="button"
        class="grid w-full cursor-pointer gap-1.5 border-b border-l-2 border-default px-4 py-3 text-left text-sm transition-colors sm:px-6"
        :class="
          repository.id === selectedRepositoryId
            ? 'border-l-primary bg-primary/10'
            : 'border-l-transparent hover:border-l-primary hover:bg-primary/5'
        "
        @click="emits('select', repository.id)"
      >
        <div class="flex min-w-0 items-start justify-between gap-3">
          <div class="min-w-0">
            <h3 class="truncate text-[13px] font-semibold text-highlighted">
              {{ repository.ownerQualifiedName }}
            </h3>
            <p class="mt-0.5 truncate text-xs leading-4 text-muted">
              {{ repository.librarySummary }}
            </p>
          </div>
          <UBadge
            :label="repository.status.label"
            :color="repository.status.color"
            size="sm"
            variant="subtle"
            class="rounded-full"
          />
        </div>
      </button>
    </div>
  </section>
</template>

<script setup lang="ts">
import type { GitHubConfiguredRepository } from "@/stores/github-settings-store";

type RepositoryScope = "enabled" | "all";

type RepositoryRow = {
  id: string;
  ownerQualifiedName: string;
  librarySummary: string;
  status: { label: string; color: "success" | "warning" };
};

const props = defineProps<{
  repositories: GitHubConfiguredRepository[];
  selectedRepositoryId: string | null;
  loading: boolean;
}>();

const emits = defineEmits<{
  select: [repositoryId: string];
}>();

/**
 * View-only scope filter. Paused repositories stay reachable under "All"
 * because they still participate in prompt customization — only their webhook
 * reviews are stopped — but enabled ones are what users configure day to day.
 */
const scope = ref<RepositoryScope>("enabled");

const scopeItems = [
  { label: "Enabled", value: "enabled" },
  { label: "All", value: "all" },
];

const compactTabsUi = {
  list: "h-7 w-auto p-0.5",
  trigger: "h-6 grow-0 px-2 py-0 text-xs",
};

/**
 * Projects mapping rows into exactly what the template renders, so the `v-for`
 * makes no method calls.
 */
const rows = computed<RepositoryRow[]>(() =>
  props.repositories
    .filter((repository) => scope.value === "all" || repository.enabled)
    .map((repository) => ({
      id: repository.id,
      ownerQualifiedName: repository.ownerQualifiedName,
      librarySummary:
        repository.libraryIds.length === 0
          ? "No libraries mapped"
          : `${repository.libraryIds.length} ${repository.libraryIds.length === 1 ? "library" : "libraries"} mapped`,
      status: repository.enabled
        ? { label: "Enabled", color: "success" }
        : { label: "Paused", color: "warning" },
    })),
);
</script>
