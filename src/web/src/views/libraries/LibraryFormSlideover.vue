<template>
  <!--
  Library create/edit slideover (right side). Handles both modes:
  - Create (library prop is null): empty form, creates on submit.
  - Edit (library prop set): seeded form, updates on submit.
  Name charset: ^[A-Za-z0-9_-]+$ validated client-side.

  Once a library exists (edit mode), a compact section picker switches between
  "Library" (name/description/source/filters), "Sync status" (run history +
  sync-now, only when source-backed), Notion webhook setup, import/export, and
  delete. Create mode stays a plain non-sectioned form — there's nothing to sync
  or delete yet.
  -->
  <USlideover v-model:open="open" side="right" title="Library">
    <template #body>
      <div v-if="isEdit" class="space-y-5">
        <UFormField label="Manage..." name="library-section">
          <USelect
            v-model="activeTab"
            :items="sectionItems"
            color="neutral"
            class="w-full"
            variant="soft"
          />
        </UFormField>

        <LibraryFormFields
          v-if="activeTab === 'library'"
          :form="form"
          :submitting="submitting"
          :is-edit="isEdit"
          :repositories="repositories"
          :source-repositories="sourceRepositories"
          :name-error="nameError"
          :source="props.library?.source ?? null"
        />

        <LibrarySyncStatusTab
          v-else-if="activeTab === 'status' && isSourceBacked"
          :source="props.library?.source ?? null"
          :runs="ingestRuns"
          :loading-runs="loadingIngestRuns"
          :syncing="syncing"
          :resetting="resetting"
          @sync-now="emits('sync-now')"
          @reset-run-state="emits('reset-run-state')"
          @load-more="emits('load-more-runs')"
        />

        <LibraryNotionWebhookTab
          v-else-if="activeTab === 'notion-webhook' && isNotionSource"
          :state="notionWebhookState"
          :source="props.library?.source ?? null"
          :loading="loadingNotionWebhookState"
          :resetting="resettingNotionWebhookState"
          :full-resyncing="fullResyncing"
          @refresh="emits('load-notion-webhook-state')"
          @reset="emits('reset-notion-webhook-state')"
          @full-resync="emits('full-resync')"
          @copy="emits('copy-notion-webhook-value', $event)"
        />

        <LibraryImportExportTab
          v-else-if="activeTab === 'import-export'"
          :library-name="props.library!.name"
          @imported="emits('imported')"
        />

        <LibraryDeleteTab
          v-else-if="activeTab === 'delete'"
          :library-name="props.library!.name"
          :is-public-source="props.library?.source?.kind === 'Public'"
          :deleting="deleting"
          @confirm-delete="onConfirmDelete"
        />
      </div>

      <LibraryFormFields
        v-else
        :form="form"
        :submitting="submitting"
        :is-edit="isEdit"
        :repositories="repositories"
        :source-repositories="sourceRepositories"
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
import type { NotionWebhookStateResponse } from "@/api/generated/types/NotionWebhookStateResponse";
import type {
  GitHubConfiguredRepository,
  GitHubRepositoryMappingRow,
} from "@/stores/github-settings-store";
import LibraryFormFields, {
  type LibraryFormState,
} from "./LibraryFormFields.vue";
import LibrarySyncStatusTab from "./LibrarySyncStatusTab.vue";
import LibraryDeleteTab from "./LibraryDeleteTab.vue";
import LibraryImportExportTab from "./LibraryImportExportTab.vue";
import LibraryNotionWebhookTab from "./LibraryNotionWebhookTab.vue";

/** Payload emitted on submit — union of the create and edit shapes. */
export type LibraryFormSubmitPayload = {
  name: string;
  description?: string;
  repositoryIds: string[];
  /** Create mode only. Absent means a plain local library. */
  source?: {
    kind: "Public" | "Private" | "Notion";
    repoUrl?: string;
    repositoryId?: string;
    ownerQualifiedName?: string;
    accessToken?: string;
    includeFilters: string[];
    excludeFilters: string[];
  };
  /** Edit mode only, for an already-source-backed library. */
  includeFilters?: string[];
  excludeFilters?: string[];
  runFullResync?: boolean;
};

const props = defineProps<{
  library: LibraryResponse | null;
  repositories: GitHubConfiguredRepository[];
  sourceRepositories: GitHubRepositoryMappingRow[];
  mappedRepositoryIds: string[];
  ingestRuns: IngestRunPageResponse | null;
  loadingIngestRuns: boolean;
  syncing: boolean;
  resetting: boolean;
  fullResyncing: boolean;
  notionWebhookState: NotionWebhookStateResponse | null;
  loadingNotionWebhookState: boolean;
  resettingNotionWebhookState: boolean;
  deleting: boolean;
  submitHandler: (data: LibraryFormSubmitPayload) => Promise<void>;
}>();

const emits = defineEmits<{
  "sync-now": [];
  "reset-run-state": [];
  "full-resync": [];
  "load-more-runs": [];
  "load-notion-webhook-state": [];
  "reset-notion-webhook-state": [];
  "copy-notion-webhook-value": [value: string];
  imported: [];
  delete: [name: string];
}>();

const open = defineModel<boolean>("open", { required: true });

/** Library name charset rule (alphanumeric, dashes, underscores). */
const NAME_PATTERN = /^[A-Za-z0-9_-]+$/;
/** Loose github.com repo URL check — server re-validates authoritatively. */
const GITHUB_URL_PATTERN = /^https:\/\/github\.com\/[^/\s]+\/[^/\s]+\/?$/;

const submitting = ref(false);
type LibrarySection =
  "library" | "status" | "notion-webhook" | "import-export" | "delete";
const activeTab = ref<LibrarySection>("library");

const form = reactive<LibraryFormState>({
  name: "",
  description: "",
  selectedRepositoryIds: [],
  sourceKind: "local",
  sourceTab: "public",
  publicRepoUrl: "",
  privateRepositoryOwnerQualifiedName: undefined,
  notionAccessToken: "",
  includeFiltersText: "",
  excludeFiltersText: "",
  originalIncludeFiltersText: "",
  originalExcludeFiltersText: "",
  runFullResync: false,
});

const isEdit = computed(() => !!props.library);
const isSourceBacked = computed(() => isEdit.value && !!props.library?.source);
const isNotionSource = computed(() => props.library?.source?.kind === "Notion");

const sectionItems = computed(() => [
  { label: "Library", value: "library", icon: "i-lucide-book-open" },
  ...(isSourceBacked.value
    ? [{ label: "Sync status", value: "status", icon: "i-lucide-refresh-cw" }]
    : []),
  ...(isNotionSource.value
    ? [
        {
          label: "Notion webhook",
          value: "notion-webhook",
          icon: "i-lucide-webhook",
        },
      ]
    : []),
  {
    label: "Import / Export",
    value: "import-export",
    icon: "i-lucide-arrow-left-right",
  },
  { label: "Delete", value: "delete", icon: "i-lucide-trash-2" },
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
  if (isEdit.value || form.sourceKind === "local") return null;

  if (form.sourceKind === "notion") {
    return form.notionAccessToken.trim()
      ? null
      : "A Notion access token is required.";
  }

  if (form.sourceTab === "public") {
    if (!form.publicRepoUrl.trim()) return "A repository URL is required.";
    if (!GITHUB_URL_PATTERN.test(form.publicRepoUrl.trim())) {
      return "Must look like https://github.com/owner/repo.";
    }
    return null;
  }

  return form.privateRepositoryOwnerQualifiedName
    ? null
    : "Select a repository.";
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
    form.sourceKind =
      lib?.source?.kind === "Notion"
        ? "notion"
        : lib?.source
          ? "github"
          : "local";
    form.sourceTab = lib?.source?.kind === "Private" ? "private" : "public";
    form.publicRepoUrl =
      lib?.source?.kind === "Public" ? (lib.source.repoUrl ?? "") : "";
    form.privateRepositoryOwnerQualifiedName = undefined;
    form.notionAccessToken = "";
    form.includeFiltersText = (lib?.source?.includeFilters ?? []).join("\n");
    form.excludeFiltersText = (lib?.source?.excludeFilters ?? []).join("\n");
    form.originalIncludeFiltersText = form.includeFiltersText;
    form.originalExcludeFiltersText = form.excludeFiltersText;
    form.runFullResync = false;
  },
  { immediate: true },
);

/** Switch to the status tab automatically once a library becomes source-backed. */
watch(isSourceBacked, (backed) => {
  if (backed) {
    activeTab.value = "status";
  }
});

/** Keep the selected section valid when switching between source-backed library types. */
watch(sectionItems, (items) => {
  if (!items.some((item) => item.value === activeTab.value)) {
    activeTab.value = "library";
  }
});

watch(open, (isOpen) => {
  if (isOpen) {
    activeTab.value = "library";
  }
});

watch(
  () => [form.includeFiltersText, form.excludeFiltersText] as const,
  ([include, exclude]) => {
    const filtersDirty =
      include !== form.originalIncludeFiltersText ||
      exclude !== form.originalExcludeFiltersText;
    if (!filtersDirty) {
      form.runFullResync = false;
    }
  },
);

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

    if (!isEdit.value && form.sourceKind !== "local") {
      payload.source =
        form.sourceKind === "notion"
          ? {
              kind: "Notion",
              accessToken: form.notionAccessToken.trim(),
              includeFilters: parseFilterLines(form.includeFiltersText),
              excludeFilters: parseFilterLines(form.excludeFiltersText),
            }
          : form.sourceTab === "public"
            ? {
                kind: "Public",
                repoUrl: form.publicRepoUrl.trim(),
                includeFilters: parseFilterLines(form.includeFiltersText),
                excludeFilters: parseFilterLines(form.excludeFiltersText),
              }
            : {
                kind: "Private",
                ownerQualifiedName: form.privateRepositoryOwnerQualifiedName,
                includeFilters: parseFilterLines(form.includeFiltersText),
                excludeFilters: parseFilterLines(form.excludeFiltersText),
              };
    } else if (isEdit.value && isSourceBacked.value) {
      payload.includeFilters = parseFilterLines(form.includeFiltersText);
      payload.excludeFilters = parseFilterLines(form.excludeFiltersText);
      payload.runFullResync = form.runFullResync;
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
