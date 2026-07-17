<template>
  <!--
  Library create/edit slideover (right side). Handles both modes:
  - Create (library prop is null): empty form, creates on submit.
  - Edit (library prop set): seeded form, updates on submit.
  Name charset: ^[A-Za-z0-9_-]+$ validated client-side.

  Tabbed once a library exists (edit mode): "Library" (name/description/
  source/filters), "Sync status" (run history + sync-now, only when
  repository-sourced), "Delete" (switch-gated, name-confirmed deletion).
  Create mode stays a plain non-tabbed form — there's nothing to sync or
  delete yet.
  -->
  <USlideover v-model:open="open" side="right" title="Library">
    <template #body>
      <UTabs
        v-if="isEdit"
        v-model="activeTab"
        :items="tabItems"
        color="neutral"
        variant="link"
      >
        <template #library>
          <LibraryFormFields
            :form="form"
            :submitting="submitting"
            :is-edit="isEdit"
            :repositories="repositories"
            :name-error="nameError"
            :source="props.library?.source ?? null"
          />
        </template>

        <template v-if="isSourceBacked" #status>
          <LibrarySyncStatusTab
            :source="props.library?.source ?? null"
            :runs="ingestRuns"
            :loading-runs="loadingIngestRuns"
            :syncing="syncing"
            @sync-now="emits('sync-now')"
            @load-more="emits('load-more-runs')"
          />
        </template>

        <template #import-export>
          <LibraryImportExportTab
            :library-name="props.library!.name"
            @imported="emits('imported')"
          />
        </template>

        <template #delete>
          <LibraryDeleteTab
            :library-name="props.library!.name"
            :is-public-source="props.library?.source?.kind === 'Public'"
            :deleting="deleting"
            @confirm-delete="onConfirmDelete"
          />
        </template>
      </UTabs>

      <LibraryFormFields
        v-else
        :form="form"
        :submitting="submitting"
        :is-edit="isEdit"
        :repositories="repositories"
        :name-error="nameError"
        :source="props.library?.source ?? null"
      />
    </template>

    <template #footer>
      <div class="flex gap-3 ml-auto">
        <UButton
          label="Cancel"
          color="neutral"
          variant="ghost"
          @click="closeSlideover"
        />
        <UButton
          v-if="activeTab === 'library'"
          :label="isEdit ? 'Save' : 'Create'"
          color="neutral"
          variant="subtle"
          :loading="submitting"
          :disabled="!canSubmit"
          @click="onSubmit"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import type { LibraryResponse } from "@/api/generated/types/LibraryResponse";
import type { IngestRunPageResponse } from "@/api/generated/types/IngestRunPageResponse";
import type { GitHubConfiguredRepository } from "@/stores/github-settings-store";
import LibraryFormFields, {
  type LibraryFormState,
} from "./LibraryFormFields.vue";
import LibrarySyncStatusTab from "./LibrarySyncStatusTab.vue";
import LibraryDeleteTab from "./LibraryDeleteTab.vue";
import LibraryImportExportTab from "./LibraryImportExportTab.vue";

/** Payload emitted on submit — union of the create and edit shapes. */
export type LibraryFormSubmitPayload = {
  name: string;
  description?: string;
  repositoryIds: string[];
  /** Create mode only. Absent means a plain local library. */
  source?: {
    kind: "Public" | "Private";
    repoUrl?: string;
    repositoryId?: string;
    includeFilters: string[];
    excludeFilters: string[];
  };
  /** Edit mode only, for an already-source-backed library. */
  includeFilters?: string[];
  excludeFilters?: string[];
};

const props = defineProps<{
  library: LibraryResponse | null;
  repositories: GitHubConfiguredRepository[];
  mappedRepositoryIds: string[];
  ingestRuns: IngestRunPageResponse | null;
  loadingIngestRuns: boolean;
  syncing: boolean;
  deleting: boolean;
  submitHandler: (data: LibraryFormSubmitPayload) => Promise<void>;
}>();

const emits = defineEmits<{
  "sync-now": [];
  "load-more-runs": [];
  imported: [];
  delete: [name: string];
}>();

const open = defineModel<boolean>("open", { required: true });

/** Library name charset rule (alphanumeric, dashes, underscores). */
const NAME_PATTERN = /^[A-Za-z0-9_-]+$/;
/** Loose github.com repo URL check — server re-validates authoritatively. */
const GITHUB_URL_PATTERN = /^https:\/\/github\.com\/[^/\s]+\/[^/\s]+\/?$/;

const submitting = ref(false);
const activeTab = ref<"library" | "status" | "import-export" | "delete">("library");

const form = reactive<LibraryFormState>({
  name: "",
  description: "",
  selectedRepositoryIds: [],
  importFromGitHub: false,
  sourceTab: "public",
  publicRepoUrl: "",
  privateRepositoryId: undefined,
  includeFiltersText: "",
  excludeFiltersText: "",
});

const isEdit = computed(() => !!props.library);
const isSourceBacked = computed(() => isEdit.value && !!props.library?.source);

const tabItems = computed(() => [
  { label: "Library", value: "library", slot: "library" as const },
  ...(isSourceBacked.value
    ? [{ label: "Sync status", value: "status", slot: "status" as const }]
    : []),
  { label: "Import / Export", value: "import-export", slot: "import-export" as const },
  { label: "Delete", value: "delete", slot: "delete" as const },
]);

/** Client-side name validation error, if any. */
const nameError = computed(() => {
  if (!form.name) return null;
  if (!NAME_PATTERN.test(form.name)) {
    return "Only letters, numbers, dashes, and underscores allowed.";
  }

  return null;
});

const sourceError = computed(() => {
  if (isEdit.value || !form.importFromGitHub) return null;

  if (form.sourceTab === "public") {
    if (!form.publicRepoUrl.trim()) return "A repository URL is required.";
    if (!GITHUB_URL_PATTERN.test(form.publicRepoUrl.trim())) {
      return "Must look like https://github.com/owner/repo.";
    }
    return null;
  }

  return form.privateRepositoryId ? null : "Select a repository.";
});

const canSubmit = computed(
  () =>
    form.name.length > 0 &&
    !nameError.value &&
    !sourceError.value &&
    !submitting.value,
);

function parseFilterLines(text: string): string[] {
  return text
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/**
 * Seeds the form when editing an existing library, resets for create mode.
 * Uses the KB create/edit pattern: watch the prop and set local refs.
 */
watch(
  () => [props.library, props.mappedRepositoryIds] as const,
  ([lib, mapped]) => {
    form.name = lib?.name ?? "";
    form.description = lib?.description ?? "";
    form.selectedRepositoryIds = [...mapped];
    form.importFromGitHub = !!lib?.source;
    form.sourceTab = lib?.source?.kind === "Private" ? "private" : "public";
    form.publicRepoUrl = lib?.source?.kind === "Public" ? lib.source.repoUrl : "";
    form.privateRepositoryId = undefined;
    form.includeFiltersText = (lib?.source?.includeFilters ?? []).join("\n");
    form.excludeFiltersText = (lib?.source?.excludeFilters ?? []).join("\n");
  },
  { immediate: true },
);

/** Switch to the status tab automatically once a library becomes source-backed. */
watch(isSourceBacked, (backed) => {
  if (backed) {
    activeTab.value = "status";
  }
});

watch(open, (isOpen) => {
  if (isOpen) {
    activeTab.value = "library";
  }
});

function closeSlideover() {
  open.value = false;
}

/** Emits the submit event with name, optional description, and the source/filters shape. */
async function onSubmit() {
  if (!canSubmit.value) return;

  submitting.value = true;
  try {
    const payload: LibraryFormSubmitPayload = {
      name: form.name,
      description: form.description || undefined,
      repositoryIds: form.selectedRepositoryIds,
    };

    if (!isEdit.value && form.importFromGitHub) {
      payload.source =
        form.sourceTab === "public"
          ? {
              kind: "Public",
              repoUrl: form.publicRepoUrl.trim(),
              includeFilters: parseFilterLines(form.includeFiltersText),
              excludeFilters: parseFilterLines(form.excludeFiltersText),
            }
          : {
              kind: "Private",
              repositoryId: form.privateRepositoryId,
              includeFilters: parseFilterLines(form.includeFiltersText),
              excludeFilters: parseFilterLines(form.excludeFiltersText),
            };
    } else if (isEdit.value && isSourceBacked.value) {
      payload.includeFilters = parseFilterLines(form.includeFiltersText);
      payload.excludeFilters = parseFilterLines(form.excludeFiltersText);
    }

    await props.submitHandler(payload);
  } finally {
    submitting.value = false;
  }
}

function onConfirmDelete() {
  if (!props.library) return;
  emits("delete", props.library.name);
}
</script>
