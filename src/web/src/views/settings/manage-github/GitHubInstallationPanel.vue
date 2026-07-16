<template>
  <!--
  GitHub App installation status panel. The parent owns route/query handling and
  emits the connect action so this child stays presentational.
  -->
  <UPageCard variant="subtle">
    <div class="flex flex-col gap-6">
      <div class="flex items-start gap-4">
        <UAvatar icon="i-hugeicons-github" size="lg" />

        <div class="min-w-0">
          <h2 class="text-base font-semibold text-highlighted">GitHub App</h2>
          <p class="mt-1 text-sm text-muted">
            Connect the active organization to the Zeeq GitHub App.
          </p>
        </div>
      </div>

      <div class="flex flex-wrap items-center justify-end gap-3">
        <UBadge
          label="Requires admin or owner"
          icon="i-hugeicons-shield-user"
          color="neutral"
          variant="outline"
          class="rounded-full"
        />

        <UBadge
          :label="statusText"
          :icon="statusIcon"
          :color="statusColor"
          :variant="statusVariant"
          class="rounded-full"
        />

        <UButton
          :label="buttonLabel"
          icon="i-hugeicons-link-square-02"
          color="neutral"
          :disabled="!canManage"
          @click="emits('connect')"
        />
      </div>
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import { computed } from "vue";

const props = defineProps<{
  canManage: boolean;
  installationLinked: boolean;
}>();

const emits = defineEmits<{
  connect: [];
}>();

/** Shows the latest connection signal available to this phase-one UI. */
const statusText = computed(() =>
  props.installationLinked ? "Connected" : "Not connected",
);

/** Makes the connected state stand out while keeping the empty state quiet. */
const statusIcon = computed(() =>
  props.installationLinked ? "i-hugeicons-tick-02" : "i-hugeicons-link-04",
);
const statusColor = computed(() =>
  props.installationLinked ? "success" : "neutral",
);
const statusVariant = computed(() =>
  props.installationLinked ? "subtle" : "outline",
);

/** Uses reconnect wording after a successful callback returns to this page. */
const buttonLabel = computed(() =>
  props.installationLinked ? "Reconnect GitHub App" : "Connect GitHub App",
);
</script>
