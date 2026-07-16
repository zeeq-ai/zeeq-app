<template>
  <!-- Fallback for OAuth browser handoff.  When a provider's callback lands on
       localhost:8096 (non-frontend host), the server stores the principal and
       redirects here with a one-time ticket.  Normally the Vite proxy forwards
       /auth/complete/* directly to the backend; this page only renders without it. -->
  <div class="flex flex-col items-center justify-center gap-4 p-4">
    <UPageCard class="w-full max-w-md">
      <div v-if="!error" class="flex flex-col items-center gap-4">
        <UIcon name="i-hugeicons-loading-02" class="size-8 animate-spin" />
        <p class="text-dimmed">Completing sign-in...</p>
      </div>
      <div v-else class="flex flex-col items-center gap-4">
        <UIcon name="i-hugeicons-alert-02" class="size-8 text-error" />
        <p class="text-error">{{ error }}</p>
        <UButton to="/login">Back to login</UButton>
      </div>
    </UPageCard>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { useRoute } from "vue-router";

const route = useRoute();
const error = ref<string | null>(null);

/**
 * Redirects to /auth/complete/{provider}?ticket=...
 * so the server can consume the ticket and set the cookie.
 */
onMounted(() => {
  const ticket = route.query.ticket as string;
  const provider = route.params.provider as string;

  if (!ticket) {
    error.value = "Missing handoff ticket.";
    return;
  }
  if (!provider) {
    error.value = "Missing provider parameter.";
    return;
  }

  window.location.href = `/auth/complete/${encodeURIComponent(provider)}?ticket=${encodeURIComponent(ticket)}`;
});
</script>
