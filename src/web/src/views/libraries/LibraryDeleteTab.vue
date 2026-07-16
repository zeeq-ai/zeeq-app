<template>
  <div class="flex flex-col gap-4 pt-2">
    <div class="rounded-md border border-error/50 bg-error/5 p-3 text-sm">
      <p class="font-semibold text-error">This cannot be undone.</p>
      <p class="mt-1 opacity-75">
        Permanently deletes this library and all its documents.
        <template v-if="isPublicSource">
          This only unsubscribes — the shared document set remains for other
          organizations subscribed to the same source.
        </template>
      </p>
    </div>

    <UFormField
      label="Enable deletion"
      description="Confirm you want to delete this library before entering its name below."
    >
      <USwitch v-model="enabled" />
    </UFormField>

    <UFormField
      :label="`Type &quot;${libraryName}&quot; to confirm`"
      description="Must match the library name exactly."
    >
      <UInput
        v-model="confirmName"
        :placeholder="libraryName"
        :disabled="!enabled"
        class="w-full"
      />
    </UFormField>

    <UButton
      label="Delete library"
      icon="i-hugeicons-delete-02"
      color="error"
      variant="subtle"
      class="self-end"
      :loading="deleting"
      :disabled="!canDelete"
      @click="emits('confirm-delete')"
    />
  </div>
</template>

<script setup lang="ts">
const props = defineProps<{
  libraryName: string;
  isPublicSource: boolean;
  deleting: boolean;
}>();

const emits = defineEmits<{
  "confirm-delete": [];
}>();

const enabled = ref(false);
const confirmName = ref("");

const canDelete = computed(
  () =>
    enabled.value && confirmName.value === props.libraryName && !props.deleting,
);

/** Resets the confirmation state whenever the target library changes. */
watch(
  () => props.libraryName,
  () => {
    enabled.value = false;
    confirmName.value = "";
  },
);
</script>
