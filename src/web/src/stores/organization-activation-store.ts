import { defineStore, acceptHMRUpdate } from "pinia";
import {
  OrganizationActivation,
  type OrganizationActivationExchangeResponse,
} from "@/api/generated";
import { ZeeqApiError } from "@/api/zeeq-api-client";

export const useOrganizationActivationStore = defineStore(
  "organization-activation-store",
  () => {
    const exchanging = ref(false);
    const error = ref<string | null>(null);

    async function exchange(
      key: string,
    ): Promise<OrganizationActivationExchangeResponse> {
      exchanging.value = true;
      error.value = null;

      try {
        return await OrganizationActivation.exchangeOrganizationActivationKey({
          key,
        });
      } catch (err: unknown) {
        error.value = toErrorMessage(err, "Activation failed.");
        throw err;
      } finally {
        exchanging.value = false;
      }
    }

    return {
      exchanging,
      error,
      exchange,
    };
  },
);

function toErrorMessage(err: unknown, fallback: string): string {
  if (err instanceof ZeeqApiError && err.status === 404) {
    // NOTE: Frontend capability discovery for this feature is deferred. Until
    // then, a 404 from exchange is the backend signal that activation is unavailable.
    return "Organization activation is not available.";
  }

  return err instanceof Error ? err.message : fallback;
}

if (import.meta.hot) {
  import.meta.hot.accept(
    acceptHMRUpdate(useOrganizationActivationStore, import.meta.hot),
  );
}
