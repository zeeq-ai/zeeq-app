<template>
  <div
    class="flex flex-col gap-4 border-b border-default py-4 last:border-b-0 sm:flex-row sm:items-center"
  >
    <div class="flex min-w-0 flex-1 items-start gap-3">
      <UIcon :name="icon" class="mt-0.5 size-5 shrink-0 text-muted" />
      <div class="min-w-0">
        <h3 class="text-sm font-medium text-highlighted">
          {{ title }}
        </h3>
        <p class="mt-1 text-sm text-muted">
          {{ description }}
        </p>
      </div>
    </div>

    <div class="flex shrink-0 items-center justify-end gap-2">
      <UBadge
        v-if="statusBadge"
        :color="statusBadge.color"
        variant="subtle"
        :icon="statusBadge.icon"
        class="rounded-full"
      >
        {{ statusBadge.label }} {{ statusBadge.time }}
      </UBadge>
      <UButton
        :label="buttonLabel"
        icon="i-hugeicons-play"
        color="neutral"
        variant="subtle"
        :loading="pending"
        :disabled="pending"
        @click="emits('run')"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
type DiagnosticResult = {
  success: boolean;
  completedAtUtc: Date | string;
};

const props = defineProps<{
  title: string;
  description: string;
  icon: string;
  buttonLabel: string;
  pending: boolean;
  result: DiagnosticResult | null;
}>();

const emits = defineEmits<{
  run: [];
}>();

/** Badge text mirrors the last completed run without resizing the action row. */
const statusBadge = computed(() => {
  if (!props.result) {
    return null;
  }

  return {
    color: props.result.success ? ("success" as const) : ("error" as const),
    icon: props.result.success
      ? "i-hugeicons-tick-02"
      : "i-hugeicons-cancel-01",
    label: props.result.success ? "Success" : "Failure",
    time: formatRunTime(props.result.completedAtUtc),
  };
});

function formatRunTime(value: Date | string): string {
  return new Date(value).toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}
</script>
