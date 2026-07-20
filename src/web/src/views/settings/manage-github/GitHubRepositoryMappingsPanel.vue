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
            Enable repositories for webhook-triggered code reviews, and choose
            which repositories appear as library sources.
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
          v-for="repository in repositoryRowsViewModel"
          :key="repository.key"
          class="flex flex-col gap-3 bg-default p-4 sm:flex-row sm:items-center sm:justify-between"
        >
          <div class="min-w-0">
            <div class="flex flex-wrap items-center gap-2">
              <a
                :href="repository.htmlUrl"
                target="_blank"
                rel="noreferrer"
                class="truncate text-sm font-medium text-highlighted hover:underline"
              >
                {{ repository.ownerQualifiedName }}
              </a>

              <UBadge
                :label="repository.visibilityLabel"
                color="neutral"
                variant="outline"
                class="rounded-full"
              />

              <UBadge
                :label="repository.status.label"
                :icon="repository.status.icon"
                :color="repository.status.color"
                variant="subtle"
                class="rounded-full"
              />
            </div>

            <p class="mt-1 text-xs text-muted">
              Default branch: {{ repository.defaultBranchLabel }}
            </p>
          </div>

          <!-- Right side buttons on each row -->
          <div class="flex flex-wrap items-center justify-end gap-2">
            <!-- Config is visible only after Zeeq has a local repository mapping. -->
            <UButton
              v-if="repository.showConfig"
              label="Config"
              icon="i-hugeicons-settings-05"
              color="neutral"
              variant="ghost"
              :disabled="!canManage || saving"
              @click="
                emits('manage-libraries', repository.configuredRepositoryId)
              "
            />

            <!-- Library visibility is visible for every GitHub App-accessible repository. -->
            <UTooltip :text="repository.libraryVisibility.tooltip">
              <UButton
                :icon="repository.libraryVisibility.icon"
                :aria-label="repository.libraryVisibility.label"
                color="neutral"
                variant="ghost"
                :loading="savingRepositoryId === repository.ownerQualifiedName"
                :disabled="!canManage || saving"
                @click="
                  emits(
                    'toggle-visibility',
                    repository.ownerQualifiedName,
                    repository.libraryVisibility.nextVisible,
                  )
                "
              />
            </UTooltip>

            <!-- Enable is visible when the repository is not enabled for webhooks. -->
            <UTooltip
              v-if="repository.showEnable"
              text="Enable GitHub webhook processing and code-review work for this repository."
            >
              <UButton
                label="Enable"
                icon="i-hugeicons-webhook"
                color="neutral"
                class="w-24"
                :loading="savingRepositoryId === repository.ownerQualifiedName"
                :disabled="!canManage || saving"
                @click="emits('enable', repository.ownerQualifiedName)"
              />
            </UTooltip>

            <!-- Pause is visible when webhook-triggered code reviews are enabled. -->
            <UTooltip
              v-if="repository.showPause"
              text="Pause webhook-triggered code-review work. The repository can still be used as a library source."
            >
              <UButton
                label="Pause"
                icon="i-hugeicons-pause"
                color="neutral"
                variant="soft"
                class="w-24"
                :loading="
                  savingRepositoryId === repository.configuredRepositoryId
                "
                :disabled="!canManage || saving"
                @click="emits('pause', repository.configuredRepositoryId)"
              />
            </UTooltip>

            <!-- Resume is visible when webhook-triggered code reviews are paused. -->
            <UTooltip
              v-else-if="repository.showResume"
              text="Resume webhook-triggered code-review work for this repository."
            >
              <UButton
                label="Resume"
                icon="i-hugeicons-play"
                color="success"
                variant="soft"
                class="w-24"
                :loading="
                  savingRepositoryId === repository.configuredRepositoryId
                "
                :disabled="!canManage || saving"
                @click="emits('resume', repository.configuredRepositoryId)"
              />
            </UTooltip>
          </div>
        </div>
      </div>
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import type { GitHubRepositoryMappingRow } from "@/stores/github-settings-store";

type RepositoryStatusViewModel = {
  label: string;
  icon: string;
  color: "success" | "warning" | "neutral";
};

type RepositoryLibraryVisibilityViewModel = {
  icon: string;
  label: string;
  tooltip: string;
  nextVisible: boolean;
};

type RepositoryRowViewModel = {
  key: string;
  ownerQualifiedName: string;
  htmlUrl: string;
  visibilityLabel: "Private" | "Public";
  defaultBranchLabel: string;
  status: RepositoryStatusViewModel;
  libraryVisibility: RepositoryLibraryVisibilityViewModel;
  configuredRepositoryId: string;
  showEnable: boolean;
  showPause: boolean;
  showResume: boolean;
  showConfig: boolean;
};

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
  "toggle-visibility": [
    ownerQualifiedName: string,
    visibleInLibraryPicker: boolean,
  ];
  "manage-libraries": [repositoryId: string];
}>();

/** Indicates whether any row-level mutation is currently in flight. */
const saving = computed(() => Boolean(props.savingRepositoryId));

/**
 * Projects API/store rows into the exact state the template renders. Keeping
 * this cached view model avoids method calls inside the repository `v-for`.
 */
const repositoryRowsViewModel = computed<RepositoryRowViewModel[]>(() =>
  props.rows.map(toRepositoryRowViewModel),
);

function toRepositoryRowViewModel(
  row: GitHubRepositoryMappingRow,
): RepositoryRowViewModel {
  const configuredRepositoryId = row.configuredMapping?.id ?? "";
  const isConfigured = Boolean(row.configuredMapping);
  const isEnabled = row.configuredMapping?.enabled === true;

  return {
    key: row.ownerQualifiedName,
    ownerQualifiedName: row.ownerQualifiedName,
    htmlUrl: row.htmlUrl,
    visibilityLabel: row.private ? "Private" : "Public",
    defaultBranchLabel: row.defaultBranch || "unknown",
    status: createStatusViewModel(isConfigured, isEnabled),
    libraryVisibility: createLibraryVisibilityViewModel(
      row.visibleInLibraryPicker,
    ),
    configuredRepositoryId,
    showEnable: !isConfigured,
    showPause: isEnabled,
    showResume: isConfigured && !isEnabled,
    showConfig: isConfigured,
  };
}

function createStatusViewModel(
  isConfigured: boolean,
  isEnabled: boolean,
): RepositoryStatusViewModel {
  if (!isConfigured) {
    return {
      label: "Not enabled",
      icon: "i-hugeicons-link-04",
      color: "neutral",
    };
  }

  return isEnabled
    ? { label: "Enabled", icon: "i-hugeicons-tick-02", color: "success" }
    : { label: "Paused", icon: "i-hugeicons-pause", color: "warning" };
}

function createLibraryVisibilityViewModel(
  visibleInLibraryPicker: boolean,
): RepositoryLibraryVisibilityViewModel {
  return visibleInLibraryPicker
    ? {
        icon: "i-hugeicons-view",
        label: "Hide from library source selection",
        tooltip: "Shown as an organization repository source for libraries.",
        nextVisible: false,
      }
    : {
        icon: "i-hugeicons-view-off-slash",
        label: "Show in library source selection",
        tooltip:
          "Hidden from library source selection. GitHub access and webhook settings are unchanged.",
        nextVisible: true,
      };
}
</script>
