<template>
  <USlideover v-model:open="open" side="right" title="Rename document">
    <template #body>
      <UForm :state="form" class="flex flex-col gap-4">
        <UFormField label="Current path">
          <UInput :model-value="fromPath ?? ''" disabled />
        </UFormField>

        <UFormField label="New path" required>
          <UInput
            v-model="form.toPath"
            placeholder="/docs/example.md"
            :disabled="submitting"
          />
          <template #error v-if="pathError">
            {{ pathError }}
          </template>
        </UFormField>
      </UForm>
    </template>

    <template #footer>
      <div class="flex gap-3">
        <UButton
          label="Cancel"
          color="neutral"
          variant="outline"
          :disabled="submitting"
          @click="closeSlideover"
        />
        <UButton
          label="Rename"
          color="primary"
          :loading="submitting"
          :disabled="!canSubmit"
          @click="onSubmit"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
const props = defineProps<{
  fromPath: string | null;
  submitHandler: (toPath: string) => Promise<void>;
}>();

const open = defineModel<boolean>("open", { required: true });

const submitting = ref(false);

const form = reactive({
  toPath: "",
});

const pathError = computed(() => {
  if (!form.toPath) return null;
  if (!form.toPath.startsWith("/")) return "Path must start with /.";
  if (form.toPath === props.fromPath) return "Choose a different path.";

  return null;
});

const canSubmit = computed(
  () => form.toPath.length > 0 && !pathError.value && !submitting.value,
);

watch(
  () => props.fromPath,
  (fromPath) => {
    form.toPath = fromPath ?? "";
  },
  { immediate: true },
);

function closeSlideover() {
  open.value = false;
}

async function onSubmit() {
  if (!canSubmit.value) return;

  submitting.value = true;
  try {
    await props.submitHandler(form.toPath);
  } finally {
    submitting.value = false;
  }
}
</script>
