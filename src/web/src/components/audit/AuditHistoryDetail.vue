<template>
  <div class="mt-3 space-y-4 rounded-md bg-elevated/30 p-3 text-sm">
    <p class="text-xs text-muted">
      Change recorded
      <time :datetime="detail.recordedAtUtc">
        {{ formatDateTime(detail.recordedAtUtc) }}
      </time>
    </p>

    <section
      v-if="detail.deletedLibrary"
      class="rounded-md border border-default p-3"
      aria-label="Deleted library summary"
    >
      <h3 class="font-medium text-highlighted">Deleted library</h3>
      <dl class="mt-2 grid gap-2 text-xs sm:grid-cols-2">
        <div>
          <dt class="text-muted">Name at deletion</dt>
          <dd class="break-words text-default">
            {{ detail.deletedLibrary.name }}
          </dd>
        </div>
        <div>
          <dt class="text-muted">Library ID</dt>
          <dd class="break-all font-mono text-default">
            {{ detail.deletedLibrary.id }}
          </dd>
        </div>
        <div>
          <dt class="text-muted">Documents removed</dt>
          <dd class="text-default">
            {{ detail.deletedLibrary.documentCount ?? "Not recorded" }}
          </dd>
        </div>
        <div>
          <dt class="text-muted">Repository mappings removed</dt>
          <dd class="text-default">
            {{ detail.deletedLibrary.repositoryMappingCount ?? "Not recorded" }}
          </dd>
        </div>
      </dl>
    </section>

    <section v-if="detail.sync" class="space-y-2" aria-label="Sync details">
      <p class="text-muted">
        Historical synced document content was not recorded.
      </p>
      <p
        v-if="detail.sync.sourceReference"
        class="break-all text-xs text-muted"
      >
        Source reference · {{ detail.sync.sourceReference }}
      </p>
      <p class="break-all font-mono text-xs text-muted">
        Operation ID · {{ detail.sync.operationId }}
      </p>
    </section>

    <div v-if="visibleChanges.length" class="space-y-3">
      <div
        v-for="change in visibleChanges"
        :key="change.label"
        class="grid gap-2 sm:grid-cols-3"
      >
        <p class="font-medium text-highlighted">{{ change.label }}</p>
        <template v-if="change.sensitive">
          <p class="text-muted sm:col-span-2">Secret value was not recorded.</p>
        </template>
        <template v-else>
          <div class="min-w-0">
            <p class="text-xs text-muted">Before</p>
            <p class="break-words text-default">
              {{ change.before ?? "None" }}
            </p>
          </div>
          <div class="min-w-0">
            <p class="text-xs text-muted">After</p>
            <p class="break-words text-default">{{ change.after ?? "None" }}</p>
          </div>
        </template>
      </div>
    </div>

    <div v-if="hasSnapshots" class="space-y-3">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <div>
          <p class="font-medium text-highlighted">Document versions</p>
          <p class="text-xs text-muted">
            Read-only snapshots. You can compare or copy either version.
          </p>
        </div>
        <UButton
          v-if="canCompare"
          label="Compare versions"
          color="neutral"
          variant="outline"
          size="sm"
          @click="compareOpen = true"
        />
      </div>

      <UAlert
        v-if="comparisonTooLarge"
        title="Too large for inline comparison"
        description="The full before and after versions are available with the copy and download actions below."
        icon="i-hugeicons-information-circle"
        color="neutral"
        variant="subtle"
      />

      <div class="grid gap-3 sm:grid-cols-2">
        <AuditSnapshotCard
          v-if="detail.beforeSnapshot"
          side="before"
          :snapshot="detail.beforeSnapshot"
          @copy="copySnapshot"
          @download="downloadSnapshot"
        />
        <AuditSnapshotCard
          v-if="detail.afterSnapshot"
          side="after"
          :snapshot="detail.afterSnapshot"
          @copy="copySnapshot"
          @download="downloadSnapshot"
        />
      </div>
    </div>

    <USlideover
      v-model:open="compareOpen"
      title="Compare document versions"
      description="Read-only comparison of the versions before and after this change."
      side="right"
      :ui="{ content: 'max-w-5xl' }"
    >
      <template #body>
        <div v-if="canCompare" class="p-4">
          <section class="min-w-0">
            <p class="mb-3 text-xs text-muted">
              <span class="font-medium text-highlighted"
                >Before this change</span
              >
              <span aria-hidden="true"> → </span>
              <span class="font-medium text-highlighted"
                >After this change</span
              >
            </p>
            <CodeDiff
              :old-string="detail.beforeSnapshot?.content ?? ''"
              :new-string="detail.afterSnapshot?.content ?? ''"
              output-format="side-by-side"
              :theme="diffTheme"
              class="audit-diff"
            />
          </section>
        </div>
      </template>
    </USlideover>
  </div>
</template>

<script setup lang="ts">
import { CodeDiff as RawCodeDiff } from "v-code-diff";
import { defineComponent, h } from "vue";
import { useClipboard } from "@vueuse/core";
import type {
  AuditHistoryDetail,
  AuditHistoryDocumentSnapshot,
} from "./audit-history-types";
import { canCompareAuditSnapshotsInline } from "./audit-history-types";
import AuditSnapshotCard from "./AuditSnapshotCard.vue";

const props = defineProps<{
  detail: AuditHistoryDetail;
}>();

const visibleChanges = computed(() => props.detail.changes);
const hasSnapshots = computed(
  () => !!props.detail.beforeSnapshot || !!props.detail.afterSnapshot,
);
const canCompare = computed(() =>
  canCompareAuditSnapshotsInline(
    props.detail.beforeSnapshot,
    props.detail.afterSnapshot,
  ),
);
const comparisonTooLarge = computed(
  () =>
    !!props.detail.beforeSnapshot &&
    !!props.detail.afterSnapshot &&
    !canCompare.value,
);
const compareOpen = ref(false);
const toast = useToast();
const { copy } = useClipboard({ legacy: true });
const colorMode = useColorMode();
const diffTheme = computed<"light" | "dark">(() =>
  colorMode.value === "dark" ? "dark" : "light",
);

const CodeDiff = defineComponent<{
  oldString: string;
  newString: string;
  outputFormat: "side-by-side" | "line-by-line";
  theme: "light" | "dark";
  class?: string;
}>(
  (componentProps) => () =>
    h(RawCodeDiff, {
      "old-string": componentProps.oldString,
      "new-string": componentProps.newString,
      "output-format": componentProps.outputFormat,
      theme: componentProps.theme,
      class: componentProps.class,
    }),
  {
    props: ["oldString", "newString", "outputFormat", "theme", "class"],
  },
);

function copySnapshot(snapshot: AuditHistoryDocumentSnapshot) {
  void copy(snapshot.content)
    .then(() => {
      toast.add({
        title: `${snapshot.versionLabel} copied`,
        icon: "i-hugeicons-tick-02",
        color: "success",
      });
    })
    .catch(() => {
      toast.add({
        title: "Could not copy snapshot",
        description: "Use Download to save this version instead.",
        icon: "i-hugeicons-alert-02",
        color: "error",
      });
    });
}

function downloadSnapshot(snapshot: AuditHistoryDocumentSnapshot) {
  const blob = new Blob([snapshot.content], {
    type: "text/markdown;charset=utf-8",
  });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = snapshot.fileName;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

function formatDateTime(value: string) {
  const date = new Date(value);

  return Number.isNaN(date.getTime())
    ? "Unknown time"
    : date.toLocaleString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit",
        second: "2-digit",
        timeZoneName: "short",
      });
}
</script>

<style scoped>
:deep(.audit-diff.code-diff-view) {
  max-height: 70vh;
  margin: 0;
  overflow: auto;
  width: 100%;
}
</style>
