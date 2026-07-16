<template>
  <!--
  Document editor panel wrapping md-editor-v3.
  Handles folder browsing, existing documents, and new documents.

  Completions (D-5): raw path insertion from the document path list.
  Save is always reviewed: emits `review(original, next, path)`.
  -->
  <div class="flex flex-col h-full">
    <!-- Empty state: no library selected, no document loaded, not creating new -->
    <div
      v-if="!activeLibraryName"
      class="flex-1 flex items-center justify-center"
    >
      <UEmpty
        title="No libraries yet"
        description="Create a library to start managing documents."
        icon="i-hugeicons-hierarchy-files"
      >
        <template #actions>
          <UButton
            label="Create your first library"
            color="neutral"
            variant="subtle"
            icon="i-hugeicons-plus-sign"
            @click="emits('createLibrary')"
          />
        </template>
      </UEmpty>
    </div>

    <template v-else-if="selectedFolderPath">
      <div
        class="flex h-[45px] items-center gap-3 px-3 border-b border-default shrink-0"
      >
        <span class="font-mono text-sm text-neutral-500 truncate flex-1">
          {{ selectedFolderDisplayPath }}
        </span>
      </div>

      <div class="flex-1 min-h-0 p-3">
        <UListbox
          v-if="folderBrowserItems.length > 0"
          :model-value="undefined"
          :items="folderBrowserItems"
          :ui="{ root: 'h-full', content: 'max-h-full' }"
          class="w-full h-full"
          @update:model-value="onFolderBrowserSelect"
        />

        <UEmpty
          v-else
          title="Empty folder"
          variant="naked"
          description="This folder does not contain any folders or documents."
          icon="i-hugeicons-folder-01"
        />
      </div>
    </template>

    <!-- Editor area -->
    <template v-else>
      <!-- Header row: theme toggle + test button + new-doc path inputs -->
      <div
        class="flex h-[45px] items-center gap-3 px-3 border-b border-default shrink-0"
      >
        <!-- New document: folder selector + filename input -->
        <template v-if="!document">
          <UTooltip
            :text="folderPathError ?? 'Leave empty for root.'"
            :content="{ side: 'bottom' }"
            :delay-duration="0"
            class="min-w-0 flex-1"
          >
            <UInputMenu
              v-model="folderPath"
              :items="folderItems"
              mode="autocomplete"
              placeholder="path/to/dir"
              aria-label="Folder path, leave empty for root"
              :color="folderPathError ? 'error' : 'neutral'"
              :highlight="!!folderPathError"
              size="sm"
              class="w-full"
              :ui="{ base: 'font-mono' }"
            />
          </UTooltip>
          <UTooltip
            :text="fileNameError ?? 'Use a valid markdown filename.'"
            :content="{ side: 'bottom' }"
            :delay-duration="0"
            class="min-w-0 flex-1"
          >
            <UInput
              v-model="fileName"
              placeholder="filename.md"
              aria-label="Markdown file name"
              :color="fileNameError ? 'error' : 'neutral'"
              :highlight="!!fileNameError"
              size="sm"
              class="w-full"
              :ui="{ base: 'font-mono' }"
            />
          </UTooltip>
        </template>

        <!-- Existing document: show path -->
        <span v-else class="font-mono text-sm text-neutral-500 truncate flex-1">
          {{ document.path }}
        </span>

        <UTooltip
          v-if="document"
          :text="copied ? 'Copied' : 'Copy Zeeq path'"
          :content="{ side: 'bottom' }"
          :delay-duration="0"
        >
          <UButton
            :icon="copied ? 'i-hugeicons-tick-02' : 'i-hugeicons-copy-01'"
            size="xs"
            color="neutral"
            variant="ghost"
            aria-label="Copy Zeeq path"
            @click="copy(zeeqPath)"
          />
        </UTooltip>

        <UTooltip
          v-if="document"
          text="Preview parse"
          :content="{ side: 'bottom' }"
          :delay-duration="0"
        >
          <UButton
            icon="i-hugeicons-search-list-01"
            size="xs"
            color="neutral"
            variant="ghost"
            aria-label="Preview parse"
            @click="emits('previewParse', document.path)"
          />
        </UTooltip>

        <div class="flex items-center gap-2 ml-auto">
          <!-- Remote read-only indicator -->
          <UBadge
            v-if="readonly"
            label="Read-only"
            color="warning"
            variant="soft"
            size="xs"
          />

          <!-- Code-review exclusion state: excluded docs never surface to review agents -->
          <UBadge
            v-if="document?.excludedFromCodeReviews"
            label="Review-excluded"
            color="warning"
            variant="soft"
            size="xs"
          />

          <!--
          Toggle code-review exclusion (local documents only — synced/remote docs are
          rejected by the API and a sync run owns their lifecycle). Reversible, so no
          confirmation flow; the root view calls the API and refreshes state.
          NOTE: `!readonly` IS the remote-origin guard — `readonly` is computed as
          `document.origin === "remote"`, so this control never renders for remote
          documents (flagged by code review, 2026-07-15; no separate origin check needed).
          -->
          <UTooltip
            v-if="document && !readonly"
            :text="
              document.excludedFromCodeReviews
                ? 'Include in code reviews'
                : 'Exclude from code reviews (agents will not consult this document)'
            "
            :content="{ side: 'bottom' }"
            :delay-duration="0"
          >
            <UButton
              :icon="
                document.excludedFromCodeReviews
                  ? 'i-hugeicons-view-off-slash'
                  : 'i-hugeicons-view'
              "
              size="xs"
              :color="document.excludedFromCodeReviews ? 'warning' : 'neutral'"
              variant="ghost"
              aria-label="Toggle code-review exclusion"
              @click="
                emits(
                  'toggleReviewExclusion',
                  document.id,
                  !document.excludedFromCodeReviews,
                )
              "
            />
          </UTooltip>

          <UButton
            icon="i-hugeicons-gibbous-moon"
            size="xs"
            color="neutral"
            variant="ghost"
            aria-label="Toggle editor theme"
            @click="toggleTheme"
          />
          <ZeeqPopConfirm
            v-if="document"
            title="Delete Document"
            :body="`Delete ${document.path}?`"
            confirm-label="Delete"
            icon="i-hugeicons-delete-02"
            size="xs"
            color="error"
            variant="ghost"
            aria-label="Delete document"
            @confirm="emits('delete', document.path)"
          />
          <UButton
            v-if="!readonly"
            label="Review and save"
            icon="i-hugeicons-checkmark-circle-02"
            size="xs"
            color="primary"
            variant="subtle"
            :disabled="!canReview"
            @click="onReview"
          />
        </div>
      </div>

      <!-- Markdown editor -->
      <MdEditor
        v-model="text"
        class="library-md-editor"
        language="en-US"
        preview-theme="github"
        style="height: 100%"
        :preview="false"
        :theme="editorTheme"
        :auto-fold-threshold="100"
        :toolbars-exclude="[
          'save',
          'catalog',
          'image',
          'github',
          'htmlPreview',
        ]"
        :html-preview="false"
        :no-upload-img="true"
        :read-only="readonly"
        :completions="completions"
      />
    </template>
  </div>
</template>

<script setup lang="ts">
import { MdEditor } from "md-editor-v3";
import "md-editor-v3/lib/style.css";
import type { CompletionSource } from "@codemirror/autocomplete";
import type { DocumentContentResponse } from "@/api/generated/types/DocumentContentResponse";
import { useClipboard } from "@vueuse/core";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import { useMarkdownEditorTheme } from "@/composables/useMarkdownEditorTheme";
import { useLibraryStore } from "@/stores/library-store";
import { storeToRefs } from "pinia";

const props = defineProps<{
  /** Null means new-document mode. */
  document: DocumentContentResponse | null;
  /** All document paths for completions and folder selection. */
  paths: string[];
  /** Selected tree folder path, if the folder browser should be shown. */
  selectedFolderPath: string | null;
  /** Preferred folder when entering new-document mode from the tree. */
  initialFolderPath: string;
}>();

const emits = defineEmits<{
  createLibrary: [];
  /** original markdown, next markdown, save path */
  review: [original: string, next: string, path: string];
  delete: [path: string];
  selectDocument: [path: string];
  selectFolder: [path: string];
  previewParse: [path: string];
  /** path + desired exclusion state; API call handled by the root view. */
  toggleReviewExclusion: [documentId: string, excluded: boolean];
}>();

const store = useLibraryStore();
const { activeLibraryName } = storeToRefs(store);

const { editorTheme, toggleTheme } = useMarkdownEditorTheme();

const { copied, copy } = useClipboard({ copiedDuring: 1500, legacy: true });

const zeeqPath = computed(() => {
  if (!props.document?.path) return "";
  return `zeeq://${props.document.path.replace(/^\//, "")}`;
});

// ── Editor text ─────────────────────────────────────────────────────────

const text = ref("");

/** The original content when a document is first loaded (for diff baseline). */
const originalText = ref("");

/** Whether the editor content differs from the original. */
const hasChanges = computed(() => text.value !== originalText.value);

const canReview = computed(
  () =>
    !readonly.value &&
    (props.document
      ? hasChanges.value
      : !folderPathError.value && !fileNameError.value),
);

/** Read-only gate: remote documents cannot be edited. */
const readonly = computed(() => props.document?.origin === "remote");

// ── New document path inputs ────────────────────────────────────────────

const folderPath = ref("");
const fileName = ref("");

// ── Folder browser ─────────────────────────────────────────────────────

type FolderBrowserItem = {
  label: string;
  value: string;
  icon: string;
  kind: "folder" | "file";
  type?: "item";
  class?: string;
};

const selectedFolderDisplayPath = computed(() => {
  return props.selectedFolderPath === "/" ? "(root)" : props.selectedFolderPath;
});

const selectableFolderBrowserItems = computed<FolderBrowserItem[]>(() => {
  if (!props.selectedFolderPath) return [];

  const selectedFolder = props.selectedFolderPath;
  const prefix = selectedFolder === "/" ? "/" : `${selectedFolder}/`;
  const folders = new Map<string, FolderBrowserItem>();
  const files: FolderBrowserItem[] = [];

  for (const path of props.paths) {
    if (selectedFolder !== "/" && !path.startsWith(prefix)) {
      continue;
    }

    const rest =
      selectedFolder === "/" ? path.slice(1) : path.slice(prefix.length);
    if (!rest || rest.startsWith("/")) continue;

    const [firstPart, ...remainingParts] = rest.split("/");

    if (remainingParts.length > 0) {
      const folderPath =
        selectedFolder === "/"
          ? `/${firstPart}`
          : `${selectedFolder}/${firstPart}`;
      folders.set(folderPath, {
        label: firstPart,
        value: folderPath,
        icon: "i-hugeicons-folder-01",
        kind: "folder",
        class: "font-mono",
      });
      continue;
    }

    files.push({
      label: firstPart,
      value: path,
      icon: "i-hugeicons-file-01",
      kind: "file",
      class: "font-mono",
    });
  }

  return [
    ...Array.from(folders.values()).sort((a, b) =>
      a.label.localeCompare(b.label),
    ),
    ...files.sort((a, b) => a.label.localeCompare(b.label)),
  ];
});

const folderBrowserItems = computed(() => selectableFolderBrowserItems.value);

function onFolderBrowserSelect(item: FolderBrowserItem | undefined) {
  if (!item) return;

  if (item.kind === "folder") {
    emits("selectFolder", item.value);
    return;
  }

  emits("selectDocument", item.value);
}

// ── Seed editor when document changes (KB create/edit pattern) ──────────

watch(
  [() => props.document, () => props.initialFolderPath],
  ([doc, initialFolderPath]) => {
    if (doc) {
      text.value = doc.content ?? "";
      originalText.value = doc.content ?? "";
      folderPath.value = "";
      fileName.value = "";
    } else {
      // New document: seed with front-matter template.
      text.value = NEW_DOC_TEMPLATE;
      originalText.value = NEW_DOC_TEMPLATE;
      folderPath.value = normalizeFolderInput(initialFolderPath);
      fileName.value = "";
    }
  },
  { immediate: true },
);

/** Folder path suggestions for the autocomplete input. Derived from document paths. */
const folderItems = computed(() => {
  const folders = new Set<string>();

  for (const p of props.paths) {
    const parent = p.substring(0, p.lastIndexOf("/"));
    if (parent) {
      folders.add(normalizeFolderInput(parent));
    }
  }

  return Array.from(folders).sort((a, b) => a.localeCompare(b));
});

const PATH_SEGMENT_PATTERN = /^[A-Za-z0-9_-]+$/;
const FILE_NAME_PATTERN = /^[A-Za-z0-9_-]+\.md$/;
const MAX_PATH_LENGTH = 100;
const MAX_FILE_NAME_LENGTH = 100;

const folderPathError = computed(() => {
  const path = folderPath.value.trim();
  if (!path) return null;
  if (path.length > MAX_PATH_LENGTH) {
    return "Path must be 100 characters or fewer.";
  }

  const parts = path.split("/");
  if (parts.some((part) => !PATH_SEGMENT_PATTERN.test(part))) {
    return "Use slash-separated alpha-numeric segments with dashes or underscores.";
  }

  return null;
});

const fileNameError = computed(() => {
  const name = fileName.value.trim();
  if (!name) return "File name is required.";
  if (name.length > MAX_FILE_NAME_LENGTH) {
    return "File name must be 100 characters or fewer.";
  }
  if (!FILE_NAME_PATTERN.test(name)) {
    return "Use alpha-numeric characters, dashes, underscores, and end with .md.";
  }

  return null;
});

/** Assembles the full save path from folder + filename for new docs. */
const assembledPath = computed(() => {
  if (props.document) {
    return props.document.path;
  }

  const trimmedFolder = folderPath.value.trim().replace(/\/+$/, "");
  const folder = !trimmedFolder
    ? ""
    : trimmedFolder.startsWith("/")
      ? trimmedFolder
      : `/${trimmedFolder}`;
  const normalizedFile = fileName.value.trim();

  return folder ? `${folder}/${normalizedFile}` : `/${normalizedFile}`;
});

function normalizeFolderInput(path: string) {
  return path.trim().replace(/^\/+|\/+$/g, "");
}

// ── Completions (D-5): raw path insertion ───────────────────────────────

/** Suggests document paths and inserts the raw path string on selection. */
const completions = ref<CompletionSource[]>([
  (ctx) => {
    const token = ctx.matchBefore(/[\w/.\-+]+/);
    if (!token || token.from === token.to) return null;

    const hits = props.paths.filter((p) => p.includes(token.text));
    if (hits.length === 0) return null;

    return {
      from: token.from,
      to: token.to,
      options: hits.map((p) => ({
        label: p,
        type: "text" as const,
        apply: p, // D-5: raw path, no @ wrapper
      })),
    };
  },
]);

// ── Review flow ─────────────────────────────────────────────────────────

/** Emits the review event with original, current text, and target path. */
function onReview() {
  if (!canReview.value) return;

  emits("review", originalText.value, text.value, assembledPath.value);
}

// ── New doc seed template ───────────────────────────────────────────────

const NEW_DOC_TEMPLATE = `---
keywords: some, key, words
---

# Title

Intro text

## Header 2

More text here

<labeled_code_block>

\`\`\`cs
public record SomeRecord(
  int Id,
  string Name
);
\`\`\`

</labeled_code_block>

Labeled code blocks like \`labeled_code_block\` will be indexed individually.
`;
</script>

<style scoped>
:deep(.library-md-editor.md-editor) {
  border-top: 0;
  border-left: 0;
  border-radius: 0;
}
</style>
