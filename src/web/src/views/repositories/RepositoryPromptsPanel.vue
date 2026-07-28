<template>
  <!--
  One accordion row per organization MCP prompt. Collapsed rows show only
  activation state; expanding a row loads the placeholders that prompt declares
  and the values this repository has saved for them.
  -->
  <!--
  The parent already explains what these prompts are and which header selects
  them; activation semantics live on the per-prompt switch where they apply,
  rather than as a second block of intro prose here.
  -->
  <div class="flex flex-col gap-3">
    <UAlert
      v-if="prompts.length === 0 && !loading"
      title="No organization prompts"
      description="Mark a library document as an organization skill to expose it as an MCP prompt, then return here to customize it."
      icon="i-hugeicons-ai-file"
      color="neutral"
      variant="subtle"
    />

    <div v-if="loading" class="grid gap-2">
      <USkeleton v-for="index in 3" :key="index" class="h-12 rounded-md" />
    </div>

    <UAccordion
      v-else-if="prompts.length > 0"
      v-model="expandedDocumentId"
      :items="items"
      :ui="{
        root: 'border border-default rounded-md',
        trigger: 'px-4 py-3 hover:bg-elevated/40',
        label: 'min-w-0 flex-1',
        body: 'px-4 pb-4 pt-0',
      }"
    >
      <!--
      Custom trigger content stays in the default slot so Nuxt UI keeps its
      built-in trailing chevron and open/closed rotation.
      -->
      <template #default="{ item }">
        <div class="flex min-w-0 w-full items-center gap-3">
          <div class="min-w-0">
            <p class="truncate text-sm font-medium text-highlighted">
              {{ item.label }}
            </p>
            <p
              class="mt-0.5 truncate font-mono text-[11px] font-normal text-muted"
            >
              {{ item.promptPath }}
            </p>
          </div>

          <div class="ml-auto flex shrink-0 items-center gap-2">
            <UBadge
              v-if="item.configuredValueCount > 0"
              :label="`${item.configuredValueCount} set`"
              color="primary"
              variant="subtle"
              size="sm"
              class="rounded-full"
            />
            <UBadge
              :label="item.active ? 'Active' : 'Inactive'"
              :color="item.active ? 'success' : 'neutral'"
              variant="subtle"
              size="sm"
              class="rounded-full"
            />
          </div>
        </div>
      </template>

      <template #body="{ item }">
        <div v-if="loadingDetailId === item.value" class="grid gap-2 py-2">
          <USkeleton v-for="index in 2" :key="index" class="h-20 rounded-md" />
        </div>

        <div
          v-else-if="detailViewModels[item.value]"
          class="flex flex-col gap-4"
        >
          <UAlert
            v-if="detailViewModels[item.value]!.placeholders.length === 0"
            title="No placeholders declared"
            description="This prompt has no zeeq_placeholder regions, so there is nothing for a repository to customize."
            icon="i-hugeicons-information-circle"
            color="neutral"
            variant="subtle"
          />

          <!-- Repository-level activation applies to the whole prompt override. -->
          <div
            class="grid gap-3 lg:grid-cols-[minmax(0,16rem)_minmax(0,1fr)] lg:items-start"
          >
            <div>
              <p class="text-sm font-medium text-highlighted">
                Repository override
              </p>
              <p class="mt-1 text-xs text-muted">
                Save repository-specific values for this prompt's placeholders.
                These values are kept even when the override is inactive.
              </p>
            </div>

            <div class="flex flex-wrap items-center justify-between gap-3">
              <USwitch
                :model-value="draftActive(item.value)"
                label="Active for this repository"
                :description="activeDescription(item.value)"
                @update:model-value="
                  (value: boolean) => setDraftActive(item.value, value)
                "
              />

              <UButton
                label="Save"
                color="neutral"
                variant="subtle"
                :loading="savingId === item.value"
                :disabled="savingId !== null"
                @click="onSave(item.value, item.documentId, item.libraryId)"
              />
            </div>
          </div>

          <USeparator
            v-if="detailViewModels[item.value]!.placeholders.length > 0"
            label="Set prompt placeholders"
          />

          <!-- One placeholder field per declared prompt placeholder. -->
          <div
            v-for="placeholder in detailViewModels[item.value]!.placeholders"
            :key="placeholder.name"
            class="grid gap-3 lg:grid-cols-[minmax(0,16rem)_minmax(0,1fr)] lg:items-start"
          >
            <div>
              <label
                :for="placeholder.inputId"
                class="text-sm font-medium text-highlighted"
              >
                {{ placeholder.label || placeholder.name }}
              </label>
              <p v-if="placeholder.description" class="mt-1 text-xs text-muted">
                {{ placeholder.description }}
              </p>
            </div>

            <UTextarea
              :id="placeholder.inputId"
              :model-value="draftValue(item.value, placeholder.name)"
              :placeholder="placeholder.defaultValue || 'No default value'"
              :rows="3"
              autoresize
              class="w-full"
              @update:model-value="
                (value: string | number) =>
                  setDraftValue(item.value, placeholder.name, String(value))
              "
              @focus="setDraftValueIntent(item.value, placeholder.name)"
            />
          </div>
        </div>
      </template>
    </UAccordion>
  </div>
</template>

<script setup lang="ts">
import type { RepositoryPromptSummaryResponse } from "@/api/generated/types/RepositoryPromptSummaryResponse";
import type { RepositoryPromptDetailResponse } from "@/api/generated/types/RepositoryPromptDetailResponse";
import { repositoryPromptKey } from "@/stores/repository-store";

type PromptAccordionItem = {
  value: string;
  documentId: string;
  libraryId: string;
  label: string;
  promptPath: string;
  active: boolean;
  configuredValueCount: number;
};

type PromptPlaceholderViewModel =
  RepositoryPromptDetailResponse["placeholders"][number] & {
    inputId: string;
  };

type PromptDetailViewModel = Omit<
  RepositoryPromptDetailResponse,
  "placeholders"
> & {
  placeholders: PromptPlaceholderViewModel[];
};

const props = defineProps<{
  organizationId: string | null;
  repositoryId: string;
  prompts: RepositoryPromptSummaryResponse[];
  details: Record<string, RepositoryPromptDetailResponse>;
  loading: boolean;
  loadingDetailId: string | null;
  savingId: string | null;
}>();

const emits = defineEmits<{
  expand: [documentId: string, libraryId: string];
  save: [
    documentId: string,
    libraryId: string,
    active: boolean,
    values: Record<string, string>,
  ];
}>();

const expandedDocumentId = ref<string | undefined>(undefined);

/**
 * Unsaved edits per prompt, keyed by organization/library/document then
 * placeholder name. Held separately from `details` so switching rows does not
 * discard in-progress work and a failed save leaves the user's input on screen.
 */
const drafts = ref<Record<string, Record<string, string>>>({});
const activeDrafts = ref<Record<string, boolean>>({});
const editedPlaceholders = ref<Record<string, string[]>>({});

/** Accordion rows precomputed so the template makes no calls while iterating. */
const items = computed<PromptAccordionItem[]>(() =>
  props.prompts.map((prompt) => ({
    value: promptKey(prompt.libraryId, prompt.documentId),
    documentId: prompt.documentId,
    libraryId: prompt.libraryId,
    label: prompt.title,
    promptPath: `${prompt.libraryName}${prompt.path}`,
    active: prompt.active,
    // The generated type widens int32 to `number | string`, so normalize once
    // here rather than coercing at each use in the template.
    configuredValueCount: Number(prompt.configuredValueCount),
  })),
);

/**
 * Projects loaded detail into template-ready placeholder rows. Input ids are
 * derived once here instead of called repeatedly from the `v-for`.
 */
const detailViewModels = computed<Record<string, PromptDetailViewModel>>(() =>
  Object.fromEntries(
    Object.entries(props.details).map(([key, detail]) => [
      key,
      {
        ...detail,
        placeholders: detail.placeholders.map((placeholder) => ({
          ...placeholder,
          inputId: placeholderInputId(key, placeholder.name),
        })),
      },
    ]),
  ),
);

/**
 * Expanding a row lazily loads its placeholders; the list endpoint deliberately
 * omits prompt bodies so it stays a single query.
 */
watch(expandedDocumentId, (key) => {
  if (!key || props.details[key]) return;

  const prompt = props.prompts.find(
    (item) => promptKey(item.libraryId, item.documentId) === key,
  );
  if (prompt) {
    emits("expand", prompt.documentId, prompt.libraryId);
  }
});

watch(
  () => props.repositoryId,
  () => {
    expandedDocumentId.value = undefined;
  },
);

/**
 * Seeds drafts once a prompt's detail arrives. Existing drafts win so a reload
 * cannot clobber edits the user has already typed.
 */
watch(
  () => props.details,
  (details) => {
    for (const [key, detail] of Object.entries(details)) {
      const draftKey = repositoryDraftKey(key);
      if (drafts.value[draftKey]) continue;

      const seeded: Record<string, string> = {};
      const edited = new Set<string>();
      for (const placeholder of detail.placeholders) {
        seeded[placeholder.name] = placeholder.value ?? "";
        if (placeholder.value !== null) {
          edited.add(placeholder.name);
        }
      }

      drafts.value = { ...drafts.value, [draftKey]: seeded };
      activeDrafts.value = {
        ...activeDrafts.value,
        [draftKey]: detail.active,
      };
      editedPlaceholders.value = {
        ...editedPlaceholders.value,
        [draftKey]: [...edited],
      };
    }
  },
  { deep: true, immediate: true },
);

/** Current draft text for one placeholder, falling back to its saved value. */
function draftValue(documentId: string, name: string): string {
  return drafts.value[repositoryDraftKey(documentId)]?.[name] ?? "";
}

function setDraftValue(documentId: string, name: string, value: string) {
  const draftKey = repositoryDraftKey(documentId);

  drafts.value = {
    ...drafts.value,
    [draftKey]: { ...(drafts.value[draftKey] ?? {}), [name]: value },
  };
  setDraftValueIntent(documentId, name);
}

function setDraftValueIntent(documentId: string, name: string) {
  const draftKey = repositoryDraftKey(documentId);

  editedPlaceholders.value = {
    ...editedPlaceholders.value,
    [draftKey]: [
      ...new Set([...(editedPlaceholders.value[draftKey] ?? []), name]),
    ],
  };
}

function draftActive(documentId: string): boolean {
  return activeDrafts.value[repositoryDraftKey(documentId)] ?? false;
}

function activeDescription(documentId: string): string {
  return draftActive(documentId)
    ? "Active: agents receive this repository's saved placeholder values."
    : "Inactive: agents receive the prompt's authored defaults. Saved values are kept.";
}

function setDraftActive(documentId: string, value: boolean) {
  activeDrafts.value = {
    ...activeDrafts.value,
    [repositoryDraftKey(documentId)]: value,
  };
}

function placeholderInputId(key: string, name: string): string {
  const inputKey = `${key}-${name}`.replace(/[^a-zA-Z0-9_-]/g, "-");

  return `repository-prompt-${inputKey}`;
}

/**
 * Emits the save with explicit empty strings preserved.
 *
 * NOTE: An absent key means "use the authored default"; an empty string means
 * "render nothing here". Track edited fields separately from their text so a
 * user can intentionally clear a placeholder without turning every untouched
 * default into an empty override. Focusing a field marks that placeholder as
 * intentional, which lets an already-empty field save an explicit empty-string
 * override without requiring type-then-delete.
 */
function onSave(key: string, documentId: string, libraryId: string) {
  const draftKey = repositoryDraftKey(key);
  const draft = drafts.value[draftKey] ?? {};
  const edited = new Set(editedPlaceholders.value[draftKey] ?? []);
  const values: Record<string, string> = {};

  for (const [name, value] of Object.entries(draft)) {
    if (edited.has(name)) {
      values[name] = value;
    }
  }

  emits("save", documentId, libraryId, draftActive(key), values);
}

function promptKey(libraryId: string, documentId: string): string {
  return repositoryPromptKey(props.organizationId ?? "", libraryId, documentId);
}

function repositoryDraftKey(promptKey: string): string {
  return `${props.repositoryId}:${promptKey}`;
}
</script>
