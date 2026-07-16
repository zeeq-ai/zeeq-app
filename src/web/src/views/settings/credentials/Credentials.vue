<template>
  <div class="flex flex-col gap-4">
    <UPageCard
      title="Credentials"
      description="Manage OAuth client credentials and long-lived API tokens for this organization. Secrets are shown only once at creation."
      variant="naked"
    />

    <UAlert
      v-if="credentialsStore.error"
      title="Credentials unavailable"
      :description="credentialsStore.error"
      icon="i-hugeicons-alert-02"
      color="error"
      variant="subtle"
    />

    <ClientCredentials />
    <UserTokens />
  </div>
</template>

<script setup lang="ts">
import { useCredentialsStore } from "@/stores/credentials-store";
import ClientCredentials from "./ClientCredentials.vue";
import UserTokens from "./UserTokens.vue";

const credentialsStore = useCredentialsStore();
</script>
