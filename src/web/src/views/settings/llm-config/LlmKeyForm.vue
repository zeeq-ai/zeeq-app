<template>
  <!-- API key form keeps plaintext in local refs only until the submit emit. -->
  <div class="grid gap-3">
    <UFormField
      v-if="includeName"
      label="Name"
      description="Displayed only as key metadata."
    >
      <UInput
        v-model="name"
        autocomplete="off"
        :disabled="saving"
        class="w-full"
      />
    </UFormField>

    <UFormField
      label="API key"
      description="Stored encrypted; never shown again."
      required
    >
      <UInput
        v-model="apiKey"
        type="password"
        autocomplete="off"
        :disabled="saving"
        class="w-full"
        @keydown.enter.prevent="submit"
      />
    </UFormField>

    <div class="flex justify-end gap-2">
      <UButton
        v-if="cancelLabel"
        :label="cancelLabel"
        color="neutral"
        variant="ghost"
        :disabled="saving"
        @click="cancel"
      />
      <UButton
        :label="submitLabel"
        :icon="submitIcon"
        color="neutral"
        variant="outline"
        :loading="saving"
        :disabled="!apiKey.trim()"
        @click="submit"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from "vue";

const props = withDefaults(
  defineProps<{
    submitLabel?: string;
    submitIcon?: string;
    saving: boolean;
    includeName?: boolean;
    cancelLabel?: string | null;
  }>(),
  {
    submitLabel: "Save key",
    submitIcon: "i-hugeicons-key-01",
    includeName: true,
    cancelLabel: null,
  },
);

const emits = defineEmits<{
  submit: [payload: { name: string | null; apiKey: string }];
  cancel: [];
}>();

const name = ref("");
const apiKey = ref("");

/** Emits trimmed form values and clears plaintext immediately after submit. */
function submit() {
  const trimmedApiKey = apiKey.value.trim();
  if (!trimmedApiKey) {
    return;
  }

  const trimmedName = props.includeName ? name.value.trim() : "";

  emits("submit", {
    name: trimmedName || null,
    apiKey: trimmedApiKey,
  });

  apiKey.value = "";
  if (props.includeName) {
    name.value = "";
  }
}

/** Clears transient plaintext and lets the parent leave the current form mode. */
function cancel() {
  apiKey.value = "";
  emits("cancel");
}
</script>
