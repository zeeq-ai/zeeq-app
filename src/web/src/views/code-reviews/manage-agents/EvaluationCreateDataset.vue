<template>
  <section class="mx-auto grid w-full max-w-xl gap-5 p-6 sm:p-10">
    <div class="grid gap-1">
      <h3 class="text-lg font-semibold text-highlighted">Create a dataset</h3>
      <p class="text-sm text-muted">
        Give this repository's reusable pull request set a name. You can add
        examples and expected findings after it is created.
      </p>
    </div>

    <UForm id="evaluation-dataset-create" :state="form" @submit="submit">
      <div class="grid gap-4">
        <UFormField label="Dataset name" required>
          <UInput
            v-model="form.name"
            autofocus
            maxlength="120"
            placeholder="Backend regressions"
            class="w-full"
            :disabled="creating"
          />
        </UFormField>
        <UFormField label="Description" hint="Optional">
          <UTextarea
            v-model="form.description"
            :rows="3"
            maxlength="300"
            placeholder="What should this dataset cover?"
            class="w-full"
            :disabled="creating"
          />
        </UFormField>
      </div>
    </UForm>

    <UAlert
      v-if="error"
      title="Could not create dataset"
      :description="error"
      color="error"
      variant="subtle"
    />

    <div class="flex justify-end">
      <UButton
        label="Create dataset"
        icon="i-hugeicons-plus-sign"
        color="primary"
        type="submit"
        form="evaluation-dataset-create"
        :loading="creating"
        :disabled="!form.name.trim()"
      />
    </div>
  </section>
</template>

<script setup lang="ts">
const props = defineProps<{
  creating: boolean;
  error: string | null;
}>();

const emits = defineEmits<{
  create: [name: string, description: string | undefined];
}>();

const form = reactive({ name: "", description: "" });

function submit() {
  const name = form.name.trim();
  if (props.creating || !name) {
    return;
  }

  emits("create", name, form.description.trim() || undefined);
}
</script>
