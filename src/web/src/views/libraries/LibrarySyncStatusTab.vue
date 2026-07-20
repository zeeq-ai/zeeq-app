<template>
  <div class="flex flex-col gap-4 pt-2">
    <!-- Status summary -->
    <div class="rounded-md border border-default p-3 text-sm">
      <div class="flex items-center justify-between">
        <span class="font-semibold">Status</span>
        <UBadge
          :label="statusLabel"
          size="sm"
          :color="statusColor"
          variant="subtle"
        />
      </div>
      <div class="mt-2 flex flex-col gap-1 text-xs opacity-75">
        <div v-if="source?.lastSyncedAt">
          Last synced: {{ formatDate(source.lastSyncedAt) }}
        </div>
        <div v-if="source?.nextSyncAt">
          Next scheduled sync: {{ formatDate(source.nextSyncAt) }}
        </div>
        <div v-if="source?.quarantined" class="text-warning">
          Quarantined — the upstream repository is no longer public. Documents
          are frozen, not deleted.
        </div>
      </div>
    </div>

    <div class="flex flex-wrap justify-end gap-2">
      <UButton
        label="Sync now"
        icon="i-hugeicons-refresh"
        color="neutral"
        variant="subtle"
        :loading="syncing"
        :disabled="syncing || isInFlight || source?.quarantined"
        @click="emits('sync-now')"
      />
      <UTooltip
        v-if="canReset"
        text="Clear the stuck queued/running sync state so this library can sync again."
      >
        <UButton
          label="Clear run state"
          icon="i-hugeicons-clean"
          color="warning"
          variant="subtle"
          :loading="resetting"
          :disabled="resetting"
          @click="emits('reset-run-state')"
        />
      </UTooltip>
    </div>

    <!-- Run history -->
    <div>
      <div class="mb-2 text-sm font-semibold">Sync history</div>
      <p v-if="source?.kind === 'Public'" class="mb-2 text-xs opacity-75">
        This repository is shared — sync history reflects all organizations
        subscribed to it, not just yours.
      </p>

      <div v-if="loadingRuns && !runs" class="text-sm opacity-75">Loading…</div>
      <div
        v-else-if="!runs || runs.runs.length === 0"
        class="text-sm opacity-75"
      >
        No runs yet.
      </div>
      <ul v-else class="flex flex-col gap-2">
        <li
          v-for="run in runs.runs"
          :key="run.id"
          class="rounded-md border border-default p-2 text-xs"
        >
          <div class="flex items-center justify-between">
            <span class="opacity-75">{{
              run.startedAtUtc ? formatDate(run.startedAtUtc) : ""
            }}</span>
            <div class="flex items-center gap-2">
              <span class="opacity-75">{{ run.trigger }}</span>
              <UBadge
                :label="run.status"
                size="sm"
                :color="runStatusColor(run.status)"
                variant="subtle"
              />
            </div>
          </div>
          <div class="mt-1 opacity-75">
            +{{ run.filesAdded }} ~{{ run.filesUpdated }} →{{
              run.filesMoved
            }}
            -{{ run.filesDeleted }}
            <span v-if="Number(run.filesFailed) > 0" class="text-error">
              ({{ run.filesFailed }} failed)</span
            >
          </div>
          <div v-if="run.failureMessage" class="mt-1 text-error">
            {{ run.failureMessage }}
          </div>
        </li>
      </ul>

      <UButton
        v-if="runs?.nextCursor"
        label="Load more"
        color="neutral"
        variant="ghost"
        size="sm"
        class="mt-2"
        :loading="loadingRuns"
        @click="emits('load-more')"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { LibrarySourceResponse } from "@/api/generated/types/LibrarySourceResponse";
import type { IngestRunPageResponse } from "@/api/generated/types/IngestRunPageResponse";

const props = defineProps<{
  source: LibrarySourceResponse | null;
  runs: IngestRunPageResponse | null;
  loadingRuns: boolean;
  syncing: boolean;
  resetting: boolean;
}>();

const emits = defineEmits<{
  "sync-now": [];
  "reset-run-state": [];
  "load-more": [];
}>();

const isInFlight = computed(
  () =>
    props.source?.syncStatus === "queued" ||
    props.source?.syncStatus === "running",
);

const statusLabel = computed(() => props.source?.syncStatus ?? "idle");

const canReset = computed(
  () => props.source?.kind === "Private" && isInFlight.value,
);

const statusColor = computed(() => {
  switch (props.source?.syncStatus) {
    case "running":
      return "info" as const;
    case "queued":
      return "warning" as const;
    case "paused":
      return "neutral" as const;
    default:
      return "success" as const;
  }
});

function runStatusColor(status: string) {
  switch (status) {
    case "Succeeded":
      return "success" as const;
    case "Partial":
      return "warning" as const;
    case "Failed":
      return "error" as const;
    case "Stalled":
      return "warning" as const;
    default:
      return "neutral" as const;
  }
}

function formatDate(value: Date | string) {
  const date = value instanceof Date ? value : new Date(value);
  return date.toLocaleString();
}
</script>
