<template>
  <!-- Key management side panel. The parent owns API calls; this component only edits local form state and emits operations. -->
  <USlideover
    v-model:open="open"
    title="LLM API keys"
    description="Tenant-owned keys are encrypted at rest."
  >
    <template #body>
      <div class="flex flex-col gap-6">
        <UPageCard v-if="rotatingKey" variant="subtle">
          <div class="flex flex-col gap-4">
            <div>
              <h2 class="text-base font-semibold text-highlighted">
                Rotate key
              </h2>
              <p class="mt-1 text-sm text-muted">
                Update the encrypted API key value for
                {{ llmKeyDisplayName(rotatingKey) }}.
              </p>
            </div>

            <LlmKeyForm
              submit-label="Update key"
              submit-icon="i-hugeicons-lock-sync-01"
              cancel-label="Cancel"
              :saving="savingKeyId === rotatingKey.id"
              :include-name="false"
              @cancel="cancelRotation"
              @submit="rotateKey(rotatingKey.id, $event.apiKey)"
            />
          </div>
        </UPageCard>

        <UPageCard v-if="!rotatingKey" variant="subtle">
          <div class="flex flex-col gap-4">
            <div>
              <h2 class="text-base font-semibold text-highlighted">Add key</h2>
              <p class="mt-1 text-sm text-muted">
                Plaintext is sent once, encrypted server-side, and cleared from
                the form.
              </p>
            </div>

            <LlmKeyForm
              submit-label="Add key"
              submit-icon="i-hugeicons-plus-sign"
              :saving="savingKeyId === 'new'"
              @submit="emits('create', $event)"
            />
          </div>
        </UPageCard>

        <UPageCard
          v-if="!rotatingKey"
          variant="subtle"
          :ui="{
            container: 'p-0 sm:p-0 gap-y-0',
          }"
        >
          <div
            class="flex items-center justify-between gap-3 border-b border-default p-4"
          >
            <div>
              <h2 class="text-base font-semibold text-highlighted">
                Saved keys
              </h2>
              <p class="mt-1 text-sm text-muted">
                Only metadata is available after a key is stored.
              </p>
            </div>
            <UBadge
              :label="`${keys.length}`"
              color="neutral"
              variant="subtle"
            />
          </div>

          <div v-if="keys.length === 0" class="p-4">
            <UAlert
              title="No tenant keys"
              description="Tiers can use the internal default key until a tenant-owned key is added."
              icon="i-hugeicons-key-01"
              color="neutral"
              variant="subtle"
            />
          </div>

          <ul v-else role="list" class="divide-y divide-default">
            <li
              v-for="key in keys"
              :key="key.id"
              class="flex flex-col gap-3 px-4 py-4 sm:px-6"
            >
              <div class="flex flex-wrap items-start justify-between gap-3">
                <div class="min-w-0 flex-1">
                  <div class="flex flex-wrap items-center gap-2">
                    <p class="text-sm font-medium text-highlighted">
                      {{ llmKeyDisplayName(key) }}
                    </p>

                    <UBadge
                      v-if="referencedKeyIds.includes(key.id)"
                      label="In use"
                      color="primary"
                      variant="subtle"
                      class="rounded-full"
                    />
                  </div>

                  <p class="mt-1 truncate text-xs text-muted">
                    {{ key.id }}
                  </p>
                  <p class="mt-1 text-xs text-muted">
                    Updated {{ formatDate(key.updatedAtUtc) }}
                  </p>
                </div>

                <div class="flex shrink-0 items-center justify-end gap-2">
                  <UTooltip
                    text="Update and rotate API key"
                    :content="{ side: 'left' }"
                    :delay-duration="0"
                  >
                    <UButton
                      label="Rotate"
                      icon="i-hugeicons-lock-sync-01"
                      color="neutral"
                      variant="soft"
                      :disabled="saving"
                      @click="startRotation(key.id)"
                    />
                  </UTooltip>

                  <ZeeqPopConfirm
                    title="Delete key?"
                    body="This disables the encrypted key metadata. It cannot be used by future LLM calls."
                    confirm-label="Delete"
                    icon="i-hugeicons-delete-02"
                    color="error"
                    variant="ghost"
                    :disabled="saving || referencedKeyIds.includes(key.id)"
                    @confirm="emits('delete', key.id)"
                  />
                </div>
              </div>

              <div class="flex w-full gap-2">
                <UInput
                  :model-value="renameDrafts[key.id] ?? key.name ?? ''"
                  placeholder="Key name"
                  autocomplete="off"
                  :disabled="saving"
                  class="min-w-0 flex-1"
                  @update:model-value="setRenameDraft(key.id, $event)"
                />
                <UButton
                  aria-label="Rename key"
                  icon="i-hugeicons-floppy-disk"
                  color="neutral"
                  variant="ghost"
                  square
                  :loading="savingKeyId === key.id"
                  :disabled="saving || !renameChanged(key)"
                  @click="renameKey(key)"
                />
              </div>
            </li>
          </ul>
        </UPageCard>
      </div>
    </template>
  </USlideover>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from "vue";
import ZeeqPopConfirm from "@/components/ZeeqPopConfirm.vue";
import { llmKeyDisplayName, type LlmApiKey } from "@/stores/llm-settings-store";
import LlmKeyForm from "./LlmKeyForm.vue";

const open = defineModel<boolean>("open", { required: true });

const props = defineProps<{
  keys: LlmApiKey[];
  referencedKeyIds: string[];
  savingKeyId: string | null;
}>();

const emits = defineEmits<{
  create: [payload: { name: string | null; apiKey: string }];
  rename: [keyId: string, name: string | null];
  rotate: [keyId: string, apiKey: string];
  delete: [keyId: string];
}>();

const renameDrafts = reactive<Record<string, string>>({});
const rotatingKeyId = ref<string | null>(null);

/** Indicates whether a key mutation is already in flight. */
const saving = computed(() => Boolean(props.savingKeyId));

/** Selected key metadata for the dedicated rotation form mode. */
const rotatingKey = computed(
  () => props.keys.find((key) => key.id === rotatingKeyId.value) ?? null,
);

/** Clears transient form mode when the slideover is closed. */
watch(open, (isOpen) => {
  if (!isOpen) {
    rotatingKeyId.value = null;
  }
});

/** Updates one row's local rename draft without touching backend metadata. */
function setRenameDraft(keyId: string, value: unknown) {
  renameDrafts[keyId] = typeof value === "string" ? value : "";
}

/** Emits a rename only when the local draft differs from saved metadata. */
function renameKey(key: LlmApiKey) {
  if (!renameChanged(key)) {
    return;
  }

  const draft = (renameDrafts[key.id] ?? "").trim();
  emits("rename", key.id, draft || null);
}

/** Reports whether the current draft would change the saved key name. */
function renameChanged(key: LlmApiKey): boolean {
  return (renameDrafts[key.id] ?? key.name ?? "").trim() !== (key.name ?? "");
}

/** Enters dedicated rotation mode for a saved key. */
function startRotation(keyId: string) {
  rotatingKeyId.value = keyId;
}

/** Leaves rotation mode without emitting plaintext. */
function cancelRotation() {
  rotatingKeyId.value = null;
}

/** Emits a rotate request and leaves rotation mode after plaintext is handed off. */
function rotateKey(keyId: string, apiKey: string) {
  emits("rotate", keyId, apiKey);
  rotatingKeyId.value = null;
}

/** Formats backend timestamps for compact metadata rows. */
function formatDate(value: string): string {
  if (!value) {
    return "unknown";
  }

  return new Date(value).toLocaleString();
}
</script>
