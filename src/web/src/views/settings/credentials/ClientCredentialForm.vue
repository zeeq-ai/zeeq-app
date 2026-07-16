<template>
  <USlideover
    :open="open"
    title="New client credential"
    description="Create a confidential OAuth client. The secret is shown only once."
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
            placeholder="e.g. CI build bot"
            autofocus
          />
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
            icon="i-hugeicons-key-01"
            color="neutral"
            variant="subtle"
            :loading="credentialsStore.saving"
            :disabled="!formState.displayName.trim()"
          />
        </div>
      </UForm>

      <!-- One-time secret reveal -->
      <div v-else class="flex flex-col gap-4">
        <UAlert
          title="Copy your client secret now"
          description="You won't see this secret again. Store it somewhere safe."
          icon="i-hugeicons-alert-02"
          color="warning"
          variant="subtle"
        />

        <UFormField label="Client ID">
          <UInput
            :model-value="created.clientId"
            readonly
            class="w-full"
            :ui="{ base: 'font-mono text-xs' }"
          >
            <template #trailing>
              <UButton
                icon="i-hugeicons-copy-01"
                color="neutral"
                variant="subtle"
                size="xs"
                @click="copy(created.clientId)"
              />
            </template>
          </UInput>
        </UFormField>

        <UFormField label="Client secret">
          <UTextarea
            :model-value="reveal ? created.clientSecret : '••••••••••••••••'"
            class="w-full"
            readonly
            :maxrows="8"
            :autoresize="true"
            :ui="{ base: 'font-mono' }"
          />

          <div class="mt-2 flex justify-end gap-2">
            <UButton
              :icon="reveal ? 'i-hugeicons-view-off' : 'i-hugeicons-view'"
              color="neutral"
              variant="subtle"
              size="xs"
              @click="reveal = !reveal"
            />
            <UButton
              icon="i-hugeicons-copy-01"
              color="neutral"
              variant="subtle"
              size="xs"
              @click="copy(created.clientSecret)"
            />
          </div>
        </UFormField>

        <UButton
          block
          color="neutral"
          variant="subtle"
          icon="i-hugeicons-copy-01"
          label="Copy all as JSON"
          @click="copyAllAsJson(created)"
        />
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
import { ref, watch } from "vue";
import { useCredentialsStore } from "@/stores/credentials-store";
import type { ClientCredentialCreated } from "@/api/generated/types/ClientCredentialCreated";

const open = defineModel<boolean>("open", { default: false });
const emit = defineEmits<{ "update:open": [value: boolean] }>();

const toast = useToast();
const credentialsStore = useCredentialsStore();

const formState = reactive({ displayName: "" });
const created = ref<ClientCredentialCreated | null>(null);
const reveal = ref(false);

// Reset state each time the slideover opens.
watch(open, (value) => {
  if (value) {
    formState.displayName = "";
    created.value = null;
    reveal.value = false;
  }
});

async function submit() {
  try {
    created.value = await credentialsStore.createClientCredential(
      formState.displayName.trim(),
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

async function copyAllAsJson(credential: ClientCredentialCreated) {
  await copy(
    JSON.stringify(
      {
        clientId: credential.clientId,
        clientSecret: credential.clientSecret,
        tokenUrl: credential.tokenUrl,
      },
      null,
      2,
    ),
  );
}
</script>
