<template>
  <!-- Inline destructive-action confirmation that keeps table/list rows compact. -->
  <UPopover v-model:open="open">
    <UButton v-bind="triggerAttrs" />

    <template #content>
      <UCard class="w-72" :ui="{ header: 'flex items-center gap-2' }">
        <template #header>
          <UIcon name="i-hugeicons-alert-02" class="text-warning" />
          <span class="font-medium">{{ title }}</span>
        </template>

        <p class="text-sm text-muted">
          {{ body }}
        </p>

        <template #footer>
          <div class="flex justify-end gap-2">
            <UButton
              label="Cancel"
              size="xs"
              color="neutral"
              variant="ghost"
              @click="cancel"
            />
            <UButton
              :label="confirmLabel"
              size="xs"
              :color="confirmColor"
              :icon="confirmIcon"
              @click="confirm"
            />
          </div>
        </template>
      </UCard>
    </template>
  </UPopover>
</template>

<script setup lang="ts">
import { ref, useAttrs } from "vue";

defineOptions({
  inheritAttrs: false,
});

withDefaults(
  defineProps<{
    title: string;
    body: string;
    confirmLabel?: string;
    confirmColor?: "error" | "neutral";
    confirmIcon?: string;
  }>(),
  {
    confirmLabel: "Delete",
    confirmColor: "error",
    confirmIcon: "i-hugeicons-delete-02",
  },
);

const emits = defineEmits<{
  confirm: [];
  cancel: [];
}>();

const open = ref(false);

// Button attrs (icon/color/variant/disabled) belong on the trigger. The
// confirm/cancel listeners are declared component events and are emitted below.
const triggerAttrs = useAttrs();

/** Closes the popover without invoking the destructive action. */
function cancel() {
  open.value = false;
  emits("cancel");
}

/** Emits confirmation after the user explicitly chooses the destructive action. */
function confirm() {
  open.value = false;
  emits("confirm");
}
</script>
