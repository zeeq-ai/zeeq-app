import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Admin,
  type AdminDiagnosticResponse,
  type AdminLlmDiagnosticResponse,
  type AdminLlmEmbeddingDiagnosticResponse,
} from "@/api/generated";

/**
 * Store for system-admin diagnostics.
 *
 * The diagnostics are intentionally independent actions: one checks message
 * delivery, the other checks app-level LLM configuration. Keeping pending and
 * result state separate lets the page disable only the row currently running.
 */
export const useAdminDiagnosticsStore = defineStore(
  "admin-diagnostics-store",
  () => {
    const messageDeliveryPending = ref(false);
    const llmDefaultKeyPending = ref(false);
    const llmEmbeddingKeyPending = ref(false);
    const messageDeliveryResult = ref<AdminDiagnosticResponse | null>(null);
    const llmDefaultKeyResult = ref<AdminLlmDiagnosticResponse | null>(null);
    const llmEmbeddingKeyResult =
      ref<AdminLlmEmbeddingDiagnosticResponse | null>(null);

    /** Runs the message-delivery diagnostic and records the last result. */
    async function runMessageDelivery() {
      messageDeliveryPending.value = true;

      try {
        messageDeliveryResult.value =
          await Admin.runAdminMessageDeliveryDiagnostic();
      } catch (err: unknown) {
        messageDeliveryResult.value = failureResult(
          "Message delivery diagnostic failed.",
          err,
        );
      } finally {
        messageDeliveryPending.value = false;
      }
    }

    /** Runs the default Fast LLM diagnostic and records the last result. */
    async function runLlmDefaultKey() {
      llmDefaultKeyPending.value = true;

      try {
        llmDefaultKeyResult.value =
          await Admin.runAdminLlmDefaultKeyDiagnostic();
      } catch (err: unknown) {
        const failure = failureResult(
          "LLM default key diagnostic failed.",
          err,
        );
        llmDefaultKeyResult.value = {
          success: failure.success,
          message: failure.message,
          startedAtUtc: failure.startedAtUtc,
          completedAtUtc: failure.completedAtUtc,
          detail: failure.detail ?? null,
          joke: null,
        };
      } finally {
        llmDefaultKeyPending.value = false;
      }
    }

    /** Runs the snippet embedding key diagnostic and records the last result. */
    async function runLlmEmbeddingKey() {
      llmEmbeddingKeyPending.value = true;

      try {
        llmEmbeddingKeyResult.value =
          await Admin.runAdminLlmEmbeddingKeyDiagnostic();
      } catch (err: unknown) {
        const failure = failureResult(
          "LLM embedding key diagnostic failed.",
          err,
        );
        llmEmbeddingKeyResult.value = {
          success: failure.success,
          message: failure.message,
          startedAtUtc: failure.startedAtUtc,
          completedAtUtc: failure.completedAtUtc,
          detail: failure.detail ?? null,
          dimensions: null,
        };
      } finally {
        llmEmbeddingKeyPending.value = false;
      }
    }

    return {
      messageDeliveryPending,
      llmDefaultKeyPending,
      llmEmbeddingKeyPending,
      messageDeliveryResult,
      llmDefaultKeyResult,
      llmEmbeddingKeyResult,
      runMessageDelivery,
      runLlmDefaultKey,
      runLlmEmbeddingKey,
    };
  },
);

/** Converts transport errors into the same result shape the view already renders. */
function failureResult(message: string, err: unknown): AdminDiagnosticResponse {
  const now = new Date();

  return {
    success: false,
    message,
    startedAtUtc: now,
    completedAtUtc: now,
    detail:
      err instanceof Error ? err.message : "The diagnostic request failed.",
  };
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useAdminDiagnosticsStore, import.meta.hot),
  );
}
