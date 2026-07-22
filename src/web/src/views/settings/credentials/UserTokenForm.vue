<template>
  <USlideover
    :open="open"
    title="New API token"
    description="Issue a long-lived bearer token. The value is shown only once."
    @update:open="(value) => emit('update:open', value)"
  >
    <template #body>
      <!-- Create form -->
      <UForm
        v-if="!created"
        :state="formState"
        class="flex flex-col gap-4"
        @submit.prevent="submit"
      >
        <UFormField label="Name" name="displayName" required>
          <UInput
            v-model="formState.displayName"
            class="w-full"
            placeholder="e.g. Local CLI"
            autofocus
          />
        </UFormField>

        <UFormField
          label="Expires in (days)"
          name="expiresInDays"
          :hint="expiresInDaysLabel"
        >
          <USlider
            v-model="formState.expiresInDays"
            :min="tokenLifetimeBounds.min"
            :max="tokenLifetimeBounds.max"
            :step="tokenLifetimeBounds.step"
            tooltip
          />
          <div class="mt-2 flex justify-between text-xs text-muted">
            <span>{{ tokenLifetimeBounds.min }} days</span>
            <span>{{ tokenLifetimeBounds.max }} days</span>
          </div>
        </UFormField>

        <div class="flex justify-end gap-2">
          <UButton
            label="Cancel"
            color="neutral"
            variant="subtle"
            @click="close"
          />
          <UButton
            type="submit"
            label="Create"
            icon="i-hugeicons-key-02"
            color="neutral"
            variant="subtle"
            :loading="credentialsStore.saving"
            :disabled="!formState.displayName.trim()"
          />
        </div>
      </UForm>

      <!-- One-time token reveal -->
      <div v-else class="flex flex-col gap-4">
        <UAlert
          title="Copy your token now"
          description="You won't see this value again. Store it somewhere safe."
          icon="i-hugeicons-alert-02"
          color="warning"
          variant="subtle"
        />

        <UFormField label="Access token">
          <UTextarea
            :model-value="reveal ? created.access_token : '••••••••••••••••'"
            class="w-full"
            readonly
            :maxrows="8"
            :ui="{ base: 'font-mono text-[11px]' }"
          />

          <div class="mt-2 flex justify-end gap-2">
            <UButton
              :icon="reveal ? 'i-hugeicons-view-off' : 'i-hugeicons-view'"
              color="neutral"
              variant="subtle"
              size="xs"
              @click="
                () => {
                  reveal = !reveal;
                }
              "
            />
            <UButton
              icon="i-hugeicons-copy-01"
              color="neutral"
              variant="subtle"
              size="xs"
              @click="copy(created.access_token)"
            />
          </div>
        </UFormField>
      </div>
    </template>

    <template #footer>
      <div v-if="created" class="flex w-full justify-end">
        <UButton
          label="I've copied it — done"
          icon="i-hugeicons-tick-02"
          color="neutral"
          variant="subtle"
          @click="close"
        />
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from "vue";
import {
  useCredentialsStore,
  type UserTokenCreated,
} from "@/stores/credentials-store";

const open = defineModel<boolean>("open", { default: false });
const emit = defineEmits<{ "update:open": [value: boolean] }>();

const toast = useToast();
const credentialsStore = useCredentialsStore();

const tokenLifetimeBounds = {
  min: 30,
  default: 365,
  max: 730,
  step: 30,
} as const;

const formState = reactive<{
  displayName: string;
  expiresInDays: number;
}>({
  displayName: "",
  expiresInDays: tokenLifetimeBounds.default,
});
const created = ref<UserTokenCreated | null>(null);
const reveal = ref(false);
const expiresInDaysLabel = computed(() => `${formState.expiresInDays} days`);

// Reset state each time the slideover opens.
watch(open, (value) => {
  if (value) {
    formState.displayName = "";
    formState.expiresInDays = tokenLifetimeBounds.default;
    created.value = null;
    reveal.value = false;
  }
});

async function submit() {
  try {
    created.value = await credentialsStore.createUserToken(
      formState.displayName.trim(),
      formState.expiresInDays,
    );
    reveal.value = true;
  } catch {
    // The store sets `error`; the container surfaces it.
  }
}

function close() {
  open.value = false;
}

async function copy(value: string) {
  try {
    await navigator.clipboard.writeText(value);
    toast.add({ title: "Copied", color: "success" });
  } catch {
    toast.add({ title: "Could not copy", color: "error" });
  }
}
</script>
