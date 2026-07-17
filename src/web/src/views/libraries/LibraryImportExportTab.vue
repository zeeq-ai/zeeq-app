<template>
  <div class="flex flex-col gap-5 pt-2">
    <section class="flex flex-col gap-3">
      <div>
        <h3 class="text-sm font-semibold text-default">Export</h3>
        <p class="mt-1 text-xs text-muted">
          Only local documents are included. GitHub-imported documents are
          skipped.
        </p>
      </div>

      <URadioGroup
        v-model="exportFormat"
        :items="exportFormatItems"
        orientation="horizontal"
      />

      <UButton
        label="Export documents"
        icon="i-hugeicons-download-04"
        color="neutral"
        variant="subtle"
        class="self-end"
        :loading="exporting"
        @click="onExport"
      />
    </section>

    <USeparator />

    <section class="flex flex-col gap-3">
      <div>
        <h3 class="text-sm font-semibold text-default">Import</h3>
        <p class="mt-1 text-xs text-muted">
          Zeeq export files must be 500 KB or smaller.
        </p>
      </div>

      <UFormField label="Zeeq export file" name="import-file">
        <UFileUpload
          v-model="selectedFile"
          accept=".zeeq-export"
          label="Drop a Zeeq export file here"
          description="Only .zeeq-export files are accepted."
          :multiple="false"
          :file-image="false"
          :reset="true"
        />
      </UFormField>

      <UAlert
        v-if="errorMessage"
        color="error"
        variant="subtle"
        icon="i-hugeicons-alert-02"
        title="Import file rejected"
        :description="errorMessage"
      />

      <div v-if="preview" class="rounded-md border border-default p-3 text-sm">
        <div class="flex items-center justify-between gap-3">
          <span class="font-semibold">Preview</span>
          <UBadge
            :label="`${preview.documentCount} documents`"
            size="sm"
            color="neutral"
            variant="subtle"
          />
        </div>

        <div class="mt-3 grid gap-2 text-xs text-muted">
          <div>New: {{ preview.newPaths.length }}</div>
          <div>Overwrites: {{ preview.duplicateLocalPaths.length }}</div>
          <div>Blocked: {{ preview.blockedRemotePaths.length }}</div>
        </div>

        <UAlert
          v-if="preview.duplicateLocalPaths.length > 0"
          class="mt-3"
          color="warning"
          variant="subtle"
          icon="i-hugeicons-alert-02"
          title="Overwrite existing local documents"
          :description="formatPathList(preview.duplicateLocalPaths)"
        />

        <UAlert
          v-if="preview.blockedRemotePaths.length > 0"
          class="mt-3"
          color="error"
          variant="subtle"
          icon="i-hugeicons-alert-02"
          title="Synced document paths are blocked"
          :description="formatPathList(preview.blockedRemotePaths)"
        />

        <UCheckbox
          v-if="preview.duplicateLocalPaths.length > 0"
          v-model="confirmOverwrite"
          class="mt-3"
          label="Overwrite duplicate local documents"
        />
      </div>

      <div class="flex justify-end gap-2">
        <UButton
          label="Preview import"
          icon="i-hugeicons-search-01"
          color="neutral"
          variant="ghost"
          :loading="previewing"
          :disabled="!selectedFile"
          @click="onPreview"
        />
        <UButton
          label="Import documents"
          icon="i-hugeicons-upload-04"
          color="neutral"
          variant="subtle"
          :loading="importing"
          :disabled="!canImport"
          @click="onImport"
        />
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { ZeeqApiError } from "@/api/zeeq-api-client";
import { useLibraryStore } from "@/stores/library-store";
import type { LibraryImportPreviewResponse } from "@/api/generated/types/LibraryImportPreviewResponse";

const props = defineProps<{
  libraryName: string;
}>();

const emits = defineEmits<{
  imported: [];
}>();

const toast = useToast();
const store = useLibraryStore();

const exportFormat = ref<"zeeq" | "zip">("zeeq");
const exportFormatItems = [
  {
    label: "Zeeq export",
    value: "zeeq",
    description: "Signed package for import.",
  },
  {
    label: "Zip archive",
    value: "zip",
    description: "Standard zip for archiving.",
  },
];

const exporting = ref(false);
const previewing = ref(false);
const importing = ref(false);
const selectedFile = ref<File | null>(null);
const preview = ref<LibraryImportPreviewResponse | null>(null);
const confirmOverwrite = ref(false);
const errorMessage = ref<string | null>(null);

const canImport = computed(() => {
  if (!selectedFile.value || !preview.value || importing.value) {
    return false;
  }

  if (preview.value.blockedRemotePaths.length > 0) {
    return false;
  }

  return (
    preview.value.duplicateLocalPaths.length === 0 || confirmOverwrite.value
  );
});

async function onExport() {
  exporting.value = true;
  try {
    await store.exportLibrary(props.libraryName, exportFormat.value);
    toast.add({ title: "Export ready", color: "success" });
  } catch (err) {
    toast.add({
      title: "Export failed",
      description: errorDescription(err),
      color: "error",
    });
  } finally {
    exporting.value = false;
  }
}

watch(selectedFile, (file) => {
  preview.value = null;
  confirmOverwrite.value = false;
  errorMessage.value = null;

  if (file && file.size > 500 * 1024) {
    errorMessage.value = "Zeeq export files must be 500 KB or smaller.";
    selectedFile.value = null;
  }
});

async function onPreview() {
  if (!selectedFile.value) {
    return;
  }

  previewing.value = true;
  errorMessage.value = null;
  confirmOverwrite.value = false;
  try {
    preview.value = await store.previewLibraryImport(
      props.libraryName,
      selectedFile.value,
    );
  } catch (err) {
    preview.value = null;
    errorMessage.value = errorDescription(err);
  } finally {
    previewing.value = false;
  }
}

async function onImport() {
  if (!selectedFile.value || !canImport.value) {
    return;
  }

  importing.value = true;
  try {
    const result = await store.importLibraryDocuments(
      props.libraryName,
      selectedFile.value,
      confirmOverwrite.value,
    );
    toast.add({
      title: "Documents imported",
      description: `${result.createdCount} created, ${result.updatedCount} updated.`,
      color: "success",
    });
    preview.value = null;
    selectedFile.value = null;
    confirmOverwrite.value = false;
    emits("imported");
  } catch (err) {
    errorMessage.value = errorDescription(err);
  } finally {
    importing.value = false;
  }
}

function formatPathList(paths: string[]) {
  const visible = paths.slice(0, 5).join(", ");
  return paths.length > 5
    ? `${visible}, and ${paths.length - 5} more`
    : visible;
}

function errorDescription(err: unknown): string {
  if (err instanceof ZeeqApiError) {
    const data = err.data as { message?: string; detail?: string } | null;
    return data?.message ?? data?.detail ?? err.message;
  }

  return err instanceof Error ? err.message : "The operation failed.";
}
</script>
