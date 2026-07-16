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

    <!-- Source: create-only toggle, or a read-only summary once source-backed -->
    <template v-if="!isEdit">
      <UFormField
        label="Import from GitHub"
        description="Ingest a repository's Markdown files into this library instead of authoring documents by hand."
      >
        <USwitch v-model="form.importFromGitHub" :disabled="submitting" />
      </UFormField>

      <template v-if="form.importFromGitHub">
        <UFormField label="Source">
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
                  v-model="privateRepositoryId"
                  :items="privateRepositoryItems"
                  value-key="value"
                  placeholder="Select a repository..."
                  :disabled="submitting"
                  class="w-full"
                />
                <p
                  v-if="repositories.length === 0"
                  class="mt-1 text-xs opacity-75"
                >
                  No repositories configured. Add one under GitHub settings first.
                </p>
              </UFormField>
            </template>
          </UTabs>
        </UFormField>
      </template>
    </template>

    <UFormField
      v-else-if="source"
      label="Source"
      description="The repository URL cannot be changed. Delete and re-create this library to import a different repository."
    >
      <div class="flex items-center gap-2 rounded-md border border-default p-2 text-sm">
        <UBadge
          :label="source.kind"
          size="sm"
          :color="source.kind === 'Public' ? 'info' : 'neutral'"
          variant="subtle"
        />
        <span class="truncate opacity-75">{{ source.repoUrl }}</span>
      </div>
    </UFormField>

    <!-- Filters: shown when importing (create) or already source-backed (edit) -->
    <template v-if="(!isEdit && form.importFromGitHub) || (isEdit && source)">
      <UFormField
        label="Include paths"
        description="Glob patterns for files to ingest (e.g. docs/**/*.md), one per line. Leave empty to include everything under *.md/*.mdc/*.mdx."
      >
        <UTextarea
          v-model="form.includeFiltersText"
          placeholder="docs/**/*.md"
          :disabled="submitting"
          :rows="3"
          class="w-full font-mono text-xs"
        />
      </UFormField>

      <UFormField
        label="Exclude paths"
        description="Glob patterns to skip, checked after include, one per line. Leave empty to exclude nothing."
      >
        <UTextarea
          v-model="form.excludeFiltersText"
          placeholder="**/node_modules/**"
          :disabled="submitting"
          :rows="3"
          class="w-full font-mono text-xs"
        />
      </UFormField>
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
import type { GitHubConfiguredRepository } from "@/stores/github-settings-store";
import type { LibrarySourceResponse } from "@/api/generated/types/LibrarySourceResponse";

/** Reactive form state shared with the parent slideover. */
export type LibraryFormState = {
  name: string;
  description: string;
  selectedRepositoryIds: string[];
  importFromGitHub: boolean;
  sourceTab: "public" | "private";
  publicRepoUrl: string;
  privateRepositoryId: string | undefined;
  includeFiltersText: string;
  excludeFiltersText: string;
};

const props = defineProps<{
  form: LibraryFormState;
  submitting: boolean;
  isEdit: boolean;
  repositories: GitHubConfiguredRepository[];
  nameError: string | null;
  /** The library's current source, for the read-only summary in edit mode. */
  source: LibrarySourceResponse | null;
}>();

const source = computed(() => props.source);
const router = useRouter();

/** Sentinel value routing to the GitHub settings page instead of selecting a repository. */
const MANAGE_REPOSITORIES_VALUE = "__manage-repositories__";

const sourceTabItems = [
  { label: "Public repository", value: "public", slot: "public" as const },
  { label: "Organization repository", value: "private", slot: "private" as const },
];

/** Checkbox items for the code-review repository picker. */
const repositoryItems = computed(() =>
  props.repositories
    .filter((r) => r.enabled)
    .map((r) => ({ value: r.id, label: r.displayName })),
);

/**
 * Options for the private-source repository combobox. Unlike the reviewer
 * repositoryItems list, paused repositories are included — `enabled` only
 * gates webhook processing, not GitHub API read access, so a paused repo can
 * still be a valid one-time/scheduled content sync source.
 */
const privateRepositoryItems = computed(() => [
  ...props.repositories.map((r) => ({ value: r.id, label: r.displayName })),
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
 * Two-way binding onto `form.privateRepositoryId`. Selecting the synthetic
 * "Manage repositories" entry navigates to the GitHub settings page instead
 * of assigning it as the source repository, and never writes back to the form.
 */
const privateRepositoryId = computed<string | undefined>({
  get: () => props.form.privateRepositoryId,
  set: (value) => {
    if (value === MANAGE_REPOSITORIES_VALUE) {
      goToManageRepositories();
      return;
    }

    props.form.privateRepositoryId = value;
  },
});
</script>
