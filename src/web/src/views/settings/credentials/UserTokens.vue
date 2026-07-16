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
        <h2 class="text-base font-semibold text-highlighted">API Tokens</h2>
        <p class="mt-1 text-sm text-muted">
          Long-lived bearer tokens for scripting and CLI access. The token value
          is shown only once at creation.
        </p>
      </div>

      <UButton
        label="New token"
        icon="i-hugeicons-plus-sign"
        color="neutral"
        variant="subtle"
        class="shrink-0"
        @click="createOpen = true"
      />
    </div>

    <div
      v-if="
        credentialsStore.loadingTokens && !credentialsStore.userTokens.length
      "
      class="flex flex-col gap-2 p-4"
    >
      <USkeleton v-for="index in 2" :key="index" class="h-12 rounded-md" />
    </div>

    <UTable
      v-else-if="credentialsStore.userTokens.length"
      :data="rows"
      :columns="columns"
    >
      <template #created-cell="{ row }">
        {{ formatDate(row.original.createdAtUtc) }}
      </template>
      <template #expires-cell="{ row }">
        <span :class="isExpired(row.original) ? 'text-error' : ''">
          {{ formatDate(row.original.expiresAtUtc) }}
        </span>
      </template>
      <template #lastUsed-cell="{ row }">
        {{
          row.original.lastUsedAtUtc
            ? formatDate(row.original.lastUsedAtUtc)
            : "Never"
        }}
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
      <UIcon name="i-hugeicons-key-02" class="size-8 text-dimmed" />
      <p class="text-sm text-dimmed">No API tokens yet.</p>
      <UButton
        label="Create your first token"
        icon="i-hugeicons-plus-sign"
        color="neutral"
        variant="link"
        @click="createOpen = true"
      />
    </div>

    <UserTokenForm v-model:open="createOpen" />

    <UModal v-model:open="revokeOpen" title="Revoke API token">
      <template #body>
        <p class="text-sm text-dimmed">
          Revoke
          <span class="font-medium text-highlighted">{{
            revokeTarget?.displayName
          }}</span
          >? The token stops authenticating immediately.
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
import { computed, onMounted, ref } from "vue";
import { useCredentialsStore } from "@/stores/credentials-store";
import type { UserTokenSummary } from "@/api/generated";
import UserTokenForm from "./UserTokenForm.vue";

const toast = useToast();
const credentialsStore = useCredentialsStore();

const createOpen = ref(false);
const revokeTarget = ref<UserTokenSummary | null>(null);
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
  { id: "created", header: "Created" },
  { id: "expires", header: "Expires" },
  { id: "lastUsed", header: "Last used" },
  { id: "actions", header: "" },
];

const rows = computed(() => credentialsStore.userTokens);

onMounted(() => {
  void credentialsStore.loadUserTokens();
});

function confirmRevoke(token: UserTokenSummary) {
  revokeTarget.value = token;
}

async function doRevoke() {
  const target = revokeTarget.value;
  if (!target) {
    return;
  }

  try {
    await credentialsStore.revokeUserToken(target.id);
    toast.add({
      title: "Token revoked",
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

function isExpired(token: UserTokenSummary): boolean {
  return new Date(token.expiresAtUtc).getTime() < Date.now();
}
</script>
