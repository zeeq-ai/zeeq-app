<template>
  <UForm :state="form" class="flex flex-col gap-4">
    <UFormField
      label="Name"
      description="Use alpha-numeric characters, dashes, and underscores.  Clear, descriptive, distinct names are best."
      required
    >
      <UInput
        v-model="form.name"
        placeholder="my-library"
        :disabled="submitting"
        class="w-full"
      />
      <template #error v-if="nameError">
        {{ nameError }}
      </template>
    </UFormField>

    <UFormField
      label="Description"
      description="Not required, but strongly recommended so agents an resolve automatically."
    >
      <UTextarea
        v-model="form.description"
        placeholder="Optional description..."
        :disabled="submitting"
        :rows="3"
        :maxlength="500"
        class="w-full"
      />
    </UFormField>

    <!-- Source: create-only chooser, or a read-only summary once source-backed -->
    <template v-if="!isEdit">
      <UFormField
        label="Source"
        description="Choose whether this library is hand-authored, backed by GitHub, or backed by a Notion integration."
      >
        <UTabs
          v-model="form.sourceKind"
          :items="sourceKindItems"
          color="neutral"
          variant="pill"
          size="sm"
        >
          <template #local>
            <UAlert
              color="neutral"
              variant="subtle"
              title="Local library"
              description="Create an empty library and author documents directly in Zeeq."
              class="mt-2"
            />
          </template>

          <template #github>
            <UFormField label="GitHub source" class="pt-2">
              <UTabs
                v-model="form.sourceTab"
                :items="sourceTabItems"
                color="neutral"
                variant="pill"
                size="sm"
              >
                <template #public>
                  <UFormField
                    description="Raw GitHub clone URL, e.g. https://github.com/owner/repo. Visibility (public/private) is verified automatically before each sync."
                    class="pt-2"
                  >
                    <UInput
                      v-model="form.publicRepoUrl"
                      placeholder="https://github.com/owner/repo"
                      :disabled="submitting"
                      class="w-full"
                    />
                  </UFormField>
                </template>

                <template #private>
                  <UFormField
                    description="Only repositories your organization's GitHub App is installed on can be imported."
                    class="pt-2"
                  >
                    <USelectMenu
                      v-model="privateRepositoryOwnerQualifiedName"
                      :items="privateRepositoryItems"
                      value-key="value"
                      placeholder="Select a repository..."
                      :disabled="submitting"
                      class="w-full"
                    />
                    <p
                      v-if="sourceRepositories.length === 0"
                      class="mt-1 text-xs opacity-75"
                    >
                      No repositories are visible as library sources. Change
                      repository visibility under GitHub settings.
                    </p>
                  </UFormField>
                </template>
              </UTabs>
            </UFormField>
          </template>

          <template #notion>
            <UFormField
              label="Notion access token"
              description="Paste an internal Notion integration token. Zeeq validates it once, stores it encrypted, and never shows it again."
              required
              class="pt-2"
            >
              <UInput
                v-model="form.notionAccessToken"
                type="password"
                placeholder="secret_..."
                :disabled="submitting"
                :maxlength="4096"
                class="w-full font-mono text-xs"
              />
            </UFormField>
          </template>
        </UTabs>
      </UFormField>
    </template>

    <UFormField
      v-else-if="source"
      label="Source"
      :description="sourceSummaryDescription"
    >
      <div
        class="flex items-center gap-2 rounded-md border border-default p-2 text-sm"
      >
        <UBadge
          :label="source.kind"
          size="sm"
          :color="sourceBadgeColor"
          variant="subtle"
        />
        <span class="truncate opacity-75">{{ sourceSummaryLabel }}</span>
      </div>
    </UFormField>

    <!-- Filters: shown when importing (create) or already source-backed (edit) -->
    <template v-if="showFilters">
      <UFormField label="Include paths" :description="includeFilterDescription">
        <UTextarea
          v-model="form.includeFiltersText"
          :placeholder="includeFilterPlaceholder"
          :disabled="submitting"
          :rows="3"
          class="w-full font-mono text-xs"
        />
      </UFormField>

      <UFormField label="Exclude paths" :description="excludeFilterDescription">
        <UTextarea
          v-model="form.excludeFiltersText"
          :placeholder="excludeFilterPlaceholder"
          :disabled="submitting"
          :rows="3"
          class="w-full font-mono text-xs"
        />
      </UFormField>

      <UAlert
        v-if="showNotionFilterResyncOption"
        color="warning"
        variant="subtle"
        title="Apply filter changes to existing Notion documents"
        description="Narrowing filters can remove documents that no longer match. Without a full resync, filter changes only affect future page updates."
      />
      <UCheckbox
        v-if="showNotionFilterResyncOption"
        v-model="form.runFullResync"
        label="Run a full resync to apply these filters to existing documents"
        :disabled="submitting"
      />
    </template>

    <UFormField
      label="Repositories"
      description="Reviewer agents for these repositories will have access to this library."
    >
      <template #label>
        <div class="flex items-center justify-between gap-2">
          <span>Repositories</span>
          <UButton
            label="Manage repositories"
            icon="i-hugeicons-link-square-01"
            color="neutral"
            variant="link"
            size="xs"
            :padded="false"
            @click="goToManageRepositories"
          />
        </div>
      </template>

      <UCheckboxGroup
        v-if="repositories.length > 0"
        v-model="form.selectedRepositoryIds"
        :items="repositoryItems"
        :disabled="submitting"
      />
      <p v-else class="text-xs opacity-75">
        No repositories configured. Add one under GitHub settings first.
      </p>
    </UFormField>
  </UForm>
</template>

<script setup lang="ts">
import { useRouter } from "vue-router";
import type {
  GitHubConfiguredRepository,
  GitHubRepositoryMappingRow,
} from "@/stores/github-settings-store";
import type { LibrarySourceResponse } from "@/api/generated/types/LibrarySourceResponse";

/** Reactive form state shared with the parent slideover. */
export type LibraryFormState = {
  name: string;
  description: string;
  selectedRepositoryIds: string[];
  sourceKind: "local" | "github" | "notion";
  sourceTab: "public" | "private";
  publicRepoUrl: string;
  privateRepositoryOwnerQualifiedName: string | undefined;
  notionAccessToken: string;
  includeFiltersText: string;
  excludeFiltersText: string;
  originalIncludeFiltersText: string;
  originalExcludeFiltersText: string;
  runFullResync: boolean;
};

const props = defineProps<{
  form: LibraryFormState;
  submitting: boolean;
  isEdit: boolean;
  repositories: GitHubConfiguredRepository[];
  sourceRepositories: GitHubRepositoryMappingRow[];
  nameError: string | null;
  /** The library's current source, for the read-only summary in edit mode. */
  source: LibrarySourceResponse | null;
}>();

const source = computed(() => props.source);
const router = useRouter();

/** Sentinel value routing to the GitHub settings page instead of selecting a repository. */
const MANAGE_REPOSITORIES_VALUE = "__manage-repositories__";

const sourceKindItems = [
  { label: "Local", value: "local", slot: "local" as const },
  { label: "GitHub", value: "github", slot: "github" as const },
  { label: "Notion", value: "notion", slot: "notion" as const },
];

const sourceTabItems = [
  { label: "Public repository", value: "public", slot: "public" as const },
  {
    label: "Organization repository",
    value: "private",
    slot: "private" as const,
  },
];

const isNotionSource = computed(
  () =>
    (!props.isEdit && props.form.sourceKind === "notion") ||
    props.source?.kind === "Notion",
);

const showFilters = computed(
  () =>
    (!props.isEdit && props.form.sourceKind !== "local") ||
    (props.isEdit && !!props.source),
);

const notionFiltersDirty = computed(
  () =>
    props.form.includeFiltersText !== props.form.originalIncludeFiltersText ||
    props.form.excludeFiltersText !== props.form.originalExcludeFiltersText,
);

const showNotionFilterResyncOption = computed(
  () =>
    props.isEdit && props.source?.kind === "Notion" && notionFiltersDirty.value,
);

const sourceSummaryLabel = computed(() => {
  if (!props.source) {
    return "";
  }

  if (props.source.kind === "Notion") {
    return props.source.displayName?.trim() || "Notion";
  }

  return props.source.repoUrl ?? "";
});

const sourceSummaryDescription = computed(() =>
  props.source?.kind === "Notion"
    ? "The Notion connection cannot be changed. Delete and re-create this library to use a different integration token."
    : "The repository URL cannot be changed. Delete and re-create this library to import a different repository.",
);

const sourceBadgeColor = computed(() => {
  switch (props.source?.kind) {
    case "Public":
      return "info" as const;
    case "Notion":
      return "primary" as const;
    default:
      return "neutral" as const;
  }
});

const includeFilterDescription = computed(() =>
  isNotionSource.value
    ? "Glob patterns for resolved Notion page paths (e.g. engineering/**), one per line. Leave empty to include every visible page."
    : "Glob patterns for files to ingest (e.g. docs/**/*.md), one per line. Leave empty to include everything under *.md/*.mdc/*.mdx.",
);

const excludeFilterDescription = computed(() =>
  isNotionSource.value
    ? "Glob patterns for Notion page paths to skip, checked after include, one per line. Leave empty to exclude nothing."
    : "Glob patterns to skip, checked after include, one per line. Leave empty to exclude nothing.",
);

const includeFilterPlaceholder = computed(() =>
  isNotionSource.value ? "engineering/**" : "docs/**/*.md",
);

const excludeFilterPlaceholder = computed(() =>
  isNotionSource.value ? "archive/**" : "**/node_modules/**",
);

/** Checkbox items for the code-review repository picker. */
const repositoryItems = computed(() =>
  props.repositories
    .filter((r) => r.enabled)
    .map((r) => ({ value: r.id, label: r.displayName })),
);

/**
 * Options for the private-source repository combobox. Unlike the reviewer
 * repositoryItems list, unenabled and paused repositories are included because
 * `enabled` only gates webhook processing, not GitHub API read access.
 */
const privateRepositoryItems = computed(() => [
  ...props.sourceRepositories.map((r) => ({
    value: r.ownerQualifiedName,
    label: r.configuredMapping?.displayName ?? r.ownerQualifiedName,
  })),
  {
    value: MANAGE_REPOSITORIES_VALUE,
    label: "Manage repositories...",
  },
]);

/** Jumps to the GitHub settings page where repositories are configured/paused. */
function goToManageRepositories() {
  void router.push({ name: "SettingsGitHub" });
}

/**
 * Two-way binding onto `form.privateRepositoryOwnerQualifiedName`. Selecting the synthetic
 * "Manage repositories" entry navigates to the GitHub settings page instead
 * of assigning it as the source repository, and never writes back to the form.
 */
const privateRepositoryOwnerQualifiedName = computed<string | undefined>({
  get: () => props.form.privateRepositoryOwnerQualifiedName,
  set: (value) => {
    if (value === MANAGE_REPOSITORIES_VALUE) {
      goToManageRepositories();
      return;
    }

    props.form.privateRepositoryOwnerQualifiedName = value;
  },
});
</script>
