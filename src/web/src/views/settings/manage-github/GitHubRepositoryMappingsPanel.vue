<template>
  <!--
  Repository mapping panel. The parent owns the Pinia store and API actions;
  this component renders the installation-visible repositories and emits the
  requested mapping operation.
  -->
  <UPageCard variant="subtle">
    <div class="flex flex-col gap-4">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 class="text-base font-semibold text-highlighted">Repositories</h2>
          <p class="mt-1 text-sm text-muted">
            Choose which installed repositories can create Zeeq review work.
          </p>
        </div>

        <UButton
          label="Refresh"
          icon="i-hugeicons-refresh"
          color="neutral"
          variant="ghost"
          :loading="loading"
          @click="emits('refresh')"
        />
      </div>

      <UAlert
        v-if="!canManage"
        title="View only"
        description="Only organization owners and admins can change repository mappings."
        icon="i-hugeicons-information-circle"
        color="neutral"
        variant="subtle"
      />

      <UAlert
        v-if="error"
        title="Could not load repositories"
        :description="error"
        icon="i-hugeicons-alert-02"
        color="error"
        variant="subtle"
      />

      <div v-if="loading && rows.length === 0" class="flex flex-col gap-3">
        <USkeleton v-for="index in 3" :key="index" class="h-16 rounded-md" />
      </div>

      <UAlert
        v-else-if="rows.length === 0"
        title="No repositories found"
        description="The linked GitHub App installation has not granted Zeeq access to any repositories."
        icon="i-hugeicons-github"
        color="neutral"
        variant="subtle"
      />

      <div
        v-else
        class="divide-y divide-muted overflow-hidden rounded-md border border-muted"
      >
        <div
          v-for="row in rows"
          :key="row.ownerQualifiedName"
          class="flex flex-col gap-3 bg-default p-4 sm:flex-row sm:items-center sm:justify-between"
        >
          <div class="min-w-0">
            <div class="flex flex-wrap items-center gap-2">
              <a
                :href="row.htmlUrl"
                target="_blank"
                rel="noreferrer"
                class="truncate text-sm font-medium text-highlighted hover:underline"
              >
                {{ row.ownerQualifiedName }}
              </a>

              <UBadge
                :label="row.private ? 'Private' : 'Public'"
                color="neutral"
                variant="outline"
                class="rounded-full"
              />

              <UBadge
                :label="statusLabel(row)"
                :icon="statusIcon(row)"
                :color="statusColor(row)"
                variant="subtle"
                class="rounded-full"
              />
            </div>

            <p class="mt-1 text-xs text-muted">
              Default branch: {{ row.defaultBranch || "unknown" }}
            </p>
          </div>

          <div class="flex flex-wrap items-center justify-end gap-2">
            <UButton
              v-if="!row.configuredMapping"
              label="Enable"
              icon="i-hugeicons-link-04"
              color="neutral"
              :loading="savingRepositoryId === row.ownerQualifiedName"
              :disabled="!canManage || saving"
              @click="emits('enable', row.ownerQualifiedName)"
            />

            <UButton
              v-if="row.configuredMapping"
              label="Config"
              icon="i-hugeicons-settings-05"
              color="neutral"
              variant="ghost"
              :disabled="!canManage || saving"
              @click="emits('manage-libraries', row.configuredMapping.id)"
            />

            <UButton
              v-if="row.configuredMapping?.enabled"
              label="Pause"
              icon="i-hugeicons-pause"
              color="neutral"
              variant="soft"
              :loading="savingRepositoryId === row.configuredMapping.id"
              :disabled="!canManage || saving"
              @click="emits('pause', row.configuredMapping.id)"
            />

            <UButton
              v-else-if="row.configuredMapping"
              label="Resume"
              icon="i-hugeicons-play"
              color="success"
              variant="soft"
              :loading="savingRepositoryId === row.configuredMapping.id"
              :disabled="!canManage || saving"
              @click="emits('resume', row.configuredMapping.id)"
            />
          </div>
        </div>
      </div>
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import type { GitHubRepositoryMappingRow } from "@/stores/github-settings-store";

const props = defineProps<{
  rows: GitHubRepositoryMappingRow[];
  canManage: boolean;
  loading: boolean;
  savingRepositoryId: string | null;
  error: string | null;
}>();

const emits = defineEmits<{
  refresh: [];
  enable: [ownerQualifiedName: string];
  pause: [repositoryId: string];
  resume: [repositoryId: string];
  "manage-libraries": [repositoryId: string];
}>();

/** Indicates whether any row-level mutation is currently in flight. */
const saving = computed(() => Boolean(props.savingRepositoryId));

/** Chooses user-facing status text from the Zeeq mapping state. */
function statusLabel(row: GitHubRepositoryMappingRow): string {
  if (!row.configuredMapping) {
    return "Not enabled";
  }

  return row.configuredMapping.enabled ? "Enabled" : "Paused";
}

/** Matches status icons to the mapping state without requiring explanatory text. */
function statusIcon(row: GitHubRepositoryMappingRow): string {
  if (!row.configuredMapping) {
    return "i-hugeicons-link-04";
  }

  return row.configuredMapping.enabled
    ? "i-hugeicons-tick-02"
    : "i-hugeicons-pause";
}

/** Uses semantic badge colors to keep enabled and paused states scannable. */
function statusColor(
  row: GitHubRepositoryMappingRow,
): "success" | "warning" | "neutral" {
  if (!row.configuredMapping) {
    return "neutral";
  }

  return row.configuredMapping.enabled ? "success" : "warning";
}
</script>
