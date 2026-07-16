<template>
  <!-- Organization identity form; mutations are emitted to the root view. -->
  <UPageCard variant="subtle">
    <div class="grid gap-5">
      <UFormField
        label="Display name"
        description="Shown in navigation, member lists, and organization settings."
        :class="fieldClass"
        :ui="{ container: 'w-full' }"
        required
      >
        <UInput
          v-model="displayName"
          :disabled="!canManage || saving"
          autocomplete="off"
          class="w-full"
        />
      </UFormField>

      <USeparator />

      <UFormField
        label="Icon"
        description="PNG, JPG, or JPEG. 64 KB max."
        :class="iconFieldClass"
      >
        <div
          class="flex flex-wrap items-center gap-3"
          :class="iconControlsClass"
        >
          <UAvatar
            :src="iconUrl || undefined"
            :alt="displayName"
            icon="i-hugeicons-cube"
            size="lg"
          />
          <UFileUpload
            v-model="iconFile"
            accept=".jpg,.jpeg,.png,image/jpeg,image/png"
            :disabled="!canManage || saving"
            :dropzone="false"
            :preview="false"
            reset
          >
            <template #default="{ open }">
              <UButton
                label="Choose"
                icon="i-hugeicons-upload-01"
                color="neutral"
                variant="outline"
                :disabled="!canManage || saving"
                @click="open()"
              />
            </template>
          </UFileUpload>
          <UButton
            label="Clear"
            color="neutral"
            variant="ghost"
            :disabled="!canManage || saving || !iconUrl"
            @click="clearIcon"
          />
        </div>
      </UFormField>
    </div>
  </UPageCard>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import type { CreateOrganizationRequest } from "@/api/generated/types/CreateOrganizationRequest";
import type { OrganizationResponse } from "@/api/generated/types/OrganizationResponse";

const props = defineProps<{
  organization: OrganizationResponse | null;
  canManage: boolean;
  saving: boolean;
  condensed?: boolean;
}>();

const emits = defineEmits<{
  save: [request: CreateOrganizationRequest];
  error: [message: string];
}>();

const displayName = ref("");
const iconUrl = ref<string | null>(null);
const iconFile = ref<File | null>(null);
const fieldClass = computed(() =>
  props.condensed
    ? "grid gap-3"
    : "grid gap-4 sm:grid-cols-[minmax(0,1fr)_24rem] sm:items-start",
);
const iconFieldClass = computed(() =>
  props.condensed
    ? "grid gap-3"
    : "flex max-sm:flex-col justify-between sm:items-center gap-4",
);
const iconControlsClass = computed(() =>
  props.condensed ? "justify-end" : "",
);

/**
 * Mirrors the loaded organization into local form refs so unsaved edits remain
 * isolated until the root view submits them.
 */
watch(
  () => props.organization,
  (organization) => {
    displayName.value = organization?.displayName ?? "";
    iconUrl.value = organization?.iconUrl ?? null;
    iconFile.value = null;
  },
  { immediate: true },
);

/** Converts the selected upload into the persisted organization icon URL. */
watch(iconFile, async (file) => {
  if (!file) {
    return;
  }

  await readIcon(file);
});

/** Emits the current form state to the root settings view. */
function submit() {
  emits("save", {
    displayName: displayName.value.trim(),
    slug: null,
    iconUrl: iconUrl.value,
  });
}

/** Clears the icon by submitting a null iconUrl. */
function clearIcon() {
  iconUrl.value = null;
  iconFile.value = null;
}

/**
 * Reads a selected image into a data URL accepted by the membership API.
 */
async function readIcon(file: File) {
  if (!["image/png", "image/jpeg"].includes(file.type)) {
    emits("error", "Organization icons must be PNG or JPG images.");
    iconFile.value = null;
    return;
  }

  if (file.size > 65_536) {
    emits("error", "Organization icons must be 64 KB or smaller.");
    iconFile.value = null;
    return;
  }

  try {
    iconUrl.value = await readFileAsDataUrl(file);
  } catch (err: unknown) {
    emits(
      "error",
      err instanceof Error ? err.message : "Could not read the selected image.",
    );
    iconFile.value = null;
  }
}

/**
 * Converts a browser File to a data URL while preserving a typed result.
 */
function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();

    reader.addEventListener("load", () => {
      if (typeof reader.result === "string") {
        resolve(reader.result);
        return;
      }

      reject(new Error("Could not read the selected image."));
    });
    reader.addEventListener("error", () => {
      reject(new Error("Could not read the selected image."));
    });
    reader.readAsDataURL(file);
  });
}

defineExpose({
  submit,
});
</script>
