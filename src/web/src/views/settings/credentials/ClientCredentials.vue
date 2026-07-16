<template>
  <UPageCard
    variant="subtle"
    :ui="{
      container: 'p-0 sm:p-0 gap-y-0',
    }"
  >
    <div
      class="flex items-start justify-between gap-3 border-b border-default p-4"
    >
      <div class="min-w-0 flex-1">
        <h2 class="text-base font-semibold text-highlighted">
          Client Credentials
        </h2>
        <p class="mt-1 text-sm text-muted">
          Confidential OAuth clients for machine-to-machine access. Use the
          client ID and secret at the token endpoint.
        </p>
      </div>

      <UButton
        label="New credential"
        icon="i-hugeicons-plus-sign"
        color="neutral"
        variant="subtle"
        class="shrink-0"
        @click="createOpen = true"
      />
    </div>

    <div
      v-if="
        credentialsStore.loadingClients &&
        !credentialsStore.clientCredentials.length
      "
      class="flex flex-col gap-2 p-4"
    >
      <USkeleton v-for="index in 2" :key="index" class="h-12 rounded-md" />
    </div>

    <UTable
      v-else-if="credentialsStore.clientCredentials.length"
      :data="rows"
      :columns="columns"
    >
      <template #created-cell="{ row }">
        {{ formatDate(row.original.createdAtUtc) }}
      </template>
      <template #actions-cell="{ row }">
        <UButton
          label="Revoke"
          icon="i-hugeicons-delete-02"
          color="error"
          variant="ghost"
          size="xs"
          @click="confirmRevoke(row.original)"
        />
      </template>
    </UTable>

    <div v-else class="flex flex-col items-center gap-2 p-4 py-8 text-center">
      <UIcon name="i-hugeicons-key-01" class="size-8 text-dimmed" />
      <p class="text-sm text-dimmed">No client credentials yet.</p>
      <UButton
        label="Create your first credential"
        icon="i-hugeicons-plus-sign"
        color="neutral"
        variant="link"
        @click="createOpen = true"
      />
    </div>

    <ClientCredentialForm v-model:open="createOpen" />

    <UModal v-model:open="revokeOpen" title="Revoke client credential">
      <template #body>
        <p class="text-sm text-dimmed">
          Revoke
          <span class="font-medium text-highlighted">{{
            revokeTarget?.displayName
          }}</span
          >? The credential can no longer issue new tokens. Already-issued
          access tokens remain valid until their normal expiry window.
        </p>
      </template>
      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            label="Cancel"
            color="neutral"
            variant="ghost"
            @click="revokeTarget = null"
          />
          <UButton
            label="Revoke"
            color="error"
            icon="i-hugeicons-delete-02"
            :loading="credentialsStore.saving"
            @click="doRevoke"
          />
        </div>
      </template>
    </UModal>
  </UPageCard>
</template>

<script setup lang="ts">
import { computed, onMounted } from "vue";
import { useCredentialsStore } from "@/stores/credentials-store";
import type { ClientCredentialSummary } from "@/api/generated";
import ClientCredentialForm from "./ClientCredentialForm.vue";

const toast = useToast();
const credentialsStore = useCredentialsStore();

const createOpen = ref(false);
const revokeTarget = ref<ClientCredentialSummary | null>(null);
const revokeOpen = computed({
  get: () => revokeTarget.value !== null,
  set: (value) => {
    if (!value) {
      revokeTarget.value = null;
    }
  },
});

const columns = [
  { accessorKey: "displayName", header: "Name" },
  { accessorKey: "clientId", header: "Client ID" },
  { id: "created", header: "Created" },
  { id: "actions", header: "" },
];

const rows = computed(() => credentialsStore.clientCredentials);

onMounted(() => {
  void credentialsStore.loadClientCredentials();
});

function confirmRevoke(credential: ClientCredentialSummary) {
  revokeTarget.value = credential;
}

async function doRevoke() {
  const target = revokeTarget.value;
  if (!target) {
    return;
  }

  try {
    await credentialsStore.revokeClientCredential(target.clientId);
    toast.add({
      title: "Credential revoked",
      description: target.displayName,
      color: "success",
    });
    revokeTarget.value = null;
  } catch {
    // The store sets `error`; the container surfaces it.
  }
}

function formatDate(value: Date | string): string {
  return new Date(value).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
  });
}
</script>
