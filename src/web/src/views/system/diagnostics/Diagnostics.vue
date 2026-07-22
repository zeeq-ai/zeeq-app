<template>
  <div class="mx-auto flex w-full max-w-3xl flex-col gap-4">
    <UPageCard
      title="System Diagnostics"
      description="Run admin-only checks for message delivery and app-level LLM configuration."
      variant="naked"
    />

    <!-- Admin diagnostics run independently so one slow check does not block the other row. -->
    <div class="rounded-md border border-default bg-default px-4">
      <DiagnosticActionRow
        title="Message delivery diagnostic"
        description="Publishes a system message and waits for the consumer completion marker."
        icon="i-hugeicons-message-notification-01"
        button-label="Run"
        :pending="messageDeliveryPending"
        :result="messageDeliveryResult"
        @run="runMessageDelivery"
      />
      <DiagnosticActionRow
        title="LLM key configuration diagnostic"
        description="Calls the default Fast LLM client and returns the generated joke text."
        icon="i-hugeicons-artificial-intelligence-08"
        button-label="Run"
        :pending="llmDefaultKeyPending"
        :result="llmDefaultKeyResult"
        @run="runLlmDefaultKey"
      />
      <DiagnosticActionRow
        title="Snippet embedding key diagnostic"
        description="Calls the snippet embedding client and reports the returned vector dimensions."
        icon="i-hugeicons-artificial-intelligence-04"
        button-label="Run"
        :pending="llmEmbeddingKeyPending"
        :result="llmEmbeddingKeyResult"
        @run="runLlmEmbeddingKey"
      />
    </div>

    <UAlert
      v-if="llmJoke"
      title="LLM response"
      :description="llmJoke"
      icon="i-hugeicons-comment-01"
      color="neutral"
      variant="subtle"
    />

    <UAlert
      v-if="llmEmbeddingDimensions"
      title="Embedding response"
      :description="`Returned a ${llmEmbeddingDimensions}-dimension vector.`"
      icon="i-hugeicons-artificial-intelligence-04"
      color="neutral"
      variant="subtle"
    />
  </div>
</template>

<script setup lang="ts">
import { storeToRefs } from "pinia";
import { useAdminDiagnosticsStore } from "@/stores/admin-diagnostics-store";
import DiagnosticActionRow from "./DiagnosticActionRow.vue";

const toast = useToast();
const diagnosticsStore = useAdminDiagnosticsStore();
const {
  messageDeliveryPending,
  llmDefaultKeyPending,
  llmEmbeddingKeyPending,
  messageDeliveryResult,
  llmDefaultKeyResult,
  llmEmbeddingKeyResult,
} = storeToRefs(diagnosticsStore);

const llmJoke = computed(() =>
  llmDefaultKeyResult.value?.success === true
    ? llmDefaultKeyResult.value.joke
    : null,
);

const llmEmbeddingDimensions = computed(() =>
  llmEmbeddingKeyResult.value?.success === true
    ? llmEmbeddingKeyResult.value.dimensions
    : null,
);

/** Runs the queue diagnostic and reports failure through the shared toast surface. */
async function runMessageDelivery() {
  await diagnosticsStore.runMessageDelivery();
  showResultToast("Message delivery", messageDeliveryResult.value);
}

/** Runs the LLM diagnostic and leaves successful joke output in an inline alert. */
async function runLlmDefaultKey() {
  await diagnosticsStore.runLlmDefaultKey();
  showResultToast("LLM key configuration", llmDefaultKeyResult.value);
}

/** Runs the embedding diagnostic and leaves the returned dimension count in an inline alert. */
async function runLlmEmbeddingKey() {
  await diagnosticsStore.runLlmEmbeddingKey();
  showResultToast("Snippet embedding key", llmEmbeddingKeyResult.value);
}

function showResultToast(
  label: string,
  result: { success: boolean; message: string; detail?: string | null } | null,
) {
  if (!result) {
    return;
  }

  toast.add({
    title: result.success ? `${label} succeeded` : `${label} failed`,
    description: result.detail ?? result.message,
    icon: result.success ? "i-hugeicons-tick-02" : "i-hugeicons-alert-02",
    color: result.success ? "success" : "error",
  });
}
</script>
