import { defineStore, acceptHMRUpdate, storeToRefs } from "pinia";
import {
  Auth,
  type ClientCredentialSummary,
  type UserTokenSummary,
} from "@/api/generated";
import { useAppStore } from "@/stores/app-store";
import { ZeeqApiError } from "@/api/zeeq-api-client";
import type { ClientCredentialCreated } from "@/api/generated/types/ClientCredentialCreated";

/**
 * OpenIddict token response shape returned by POST /orgs/:orgId/tokens.
 *
 * The OpenAPI schema types this response as `unknown` because the handler
 * streams OpenIddict's token-endpoint JSON verbatim, so the shape is declared
 * locally for the create-token flow only.
 */
export interface UserTokenCreated {
  access_token: string;
  token_type?: string;
  expires_in?: number;
  scope?: string;
}

/**
 * Store for the Credentials settings screen.
 *
 * Wraps the generated `Auth` client for user-owned OAuth client credentials and
 * long-lived user tokens. Both are org-scoped to the active organization from
 * /me, matching the backend's `RequireRouteOrganizationMatchesCookie` gate.
 */
export const useCredentialsStore = defineStore("credentials-store", () => {
  const appStore = useAppStore();
  const { user: me } = storeToRefs(appStore);

  const clientCredentials = ref<ClientCredentialSummary[]>([]);
  const userTokens = ref<UserTokenSummary[]>([]);
  const loadingClients = ref(false);
  const loadingTokens = ref(false);
  const saving = ref(false);
  const error = ref<string | null>(null);

  const currentOrganizationId = computed(
    () => me.value?.organizationId ?? null,
  );

  /**
   * Loads the active org's client credentials. No-ops when there is no active
   * org (e.g. during sign-out) so the settings view can mount safely.
   */
  async function loadClientCredentials() {
    if (!currentOrganizationId.value) {
      clientCredentials.value = [];
      return;
    }

    loadingClients.value = true;
    error.value = null;
    try {
      clientCredentials.value = await Auth.getClientCredentials(
        currentOrganizationId.value,
      );
    } catch (err: unknown) {
      error.value = toErrorMessage(err, "Could not load client credentials.");
    } finally {
      loadingClients.value = false;
    }
  }

  /**
   * Creates a confidential OAuth client. The plaintext secret is returned once
   * and must be surfaced to the user immediately; it is not persisted here.
   */
  async function createClientCredential(
    displayName: string,
  ): Promise<ClientCredentialCreated | null> {
    if (!currentOrganizationId.value) {
      return null;
    }

    saving.value = true;
    error.value = null;
    try {
      const created = await Auth.createClientCredential(
        currentOrganizationId.value,
        { displayName },
      );
      // Prepend the new summary so the list reflects it without a refetch.
      clientCredentials.value = [
        {
          clientId: created.clientId,
          displayName: created.displayName,
          createdAtUtc: created.createdAtUtc,
          revokedAtUtc: null,
        },
        ...clientCredentials.value,
      ];
      return created;
    } catch (err: unknown) {
      error.value = toErrorMessage(err, "Could not create client credential.");
      throw err;
    } finally {
      saving.value = false;
    }
  }

  /**
   * Revokes a client credential and removes it from the local list.
   */
  async function revokeClientCredential(clientId: string) {
    if (!currentOrganizationId.value) {
      return;
    }

    saving.value = true;
    error.value = null;
    try {
      await Auth.revokeClientCredential(currentOrganizationId.value, clientId);
      clientCredentials.value = clientCredentials.value.filter(
        (credential) => credential.clientId !== clientId,
      );
    } catch (err: unknown) {
      error.value = toErrorMessage(err, "Could not revoke client credential.");
      throw err;
    } finally {
      saving.value = false;
    }
  }

  /**
   * Loads the active org's long-lived user tokens.
   */
  async function loadUserTokens() {
    if (!currentOrganizationId.value) {
      userTokens.value = [];
      return;
    }

    loadingTokens.value = true;
    error.value = null;
    try {
      userTokens.value = await Auth.getUserTokens(currentOrganizationId.value);
    } catch (err: unknown) {
      error.value = toErrorMessage(err, "Could not load API tokens.");
    } finally {
      loadingTokens.value = false;
    }
  }

  /**
   * Issues a long-lived bearer token. The plaintext token value is returned
   * once and must be surfaced to the user immediately; only metadata is
   * persisted for later list/revoke.
   */
  async function createUserToken(
    displayName: string,
    expiresInDays?: number,
  ): Promise<UserTokenCreated | null> {
    if (!currentOrganizationId.value) {
      return null;
    }

    saving.value = true;
    error.value = null;
    try {
      const response = (await Auth.createUserToken(
        currentOrganizationId.value,
        {
          displayName,
          expiresInDays: expiresInDays ?? null,
        },
        // The OpenAPI schema types this response as `unknown` because the
        // handler streams OpenIddict's token JSON verbatim.
      )) as unknown as UserTokenCreated;

      // Refresh the metadata list so the new token's row appears. The token
      // value itself is never stored, only surfaced from the create response.
      await loadUserTokens();
      return response;
    } catch (err: unknown) {
      error.value = toErrorMessage(err, "Could not create API token.");
      throw err;
    } finally {
      saving.value = false;
    }
  }

  /**
   * Revokes a long-lived user token and removes it from the local list.
   */
  async function revokeUserToken(id: string) {
    if (!currentOrganizationId.value) {
      return;
    }

    saving.value = true;
    error.value = null;
    try {
      await Auth.revokeUserToken(currentOrganizationId.value, id);
      userTokens.value = userTokens.value.filter((token) => token.id !== id);
    } catch (err: unknown) {
      error.value = toErrorMessage(err, "Could not revoke API token.");
      throw err;
    } finally {
      saving.value = false;
    }
  }

  return {
    clientCredentials,
    userTokens,
    loadingClients,
    loadingTokens,
    saving,
    error,
    currentOrganizationId,
    loadClientCredentials,
    createClientCredential,
    revokeClientCredential,
    loadUserTokens,
    createUserToken,
    revokeUserToken,
  };
});

function toErrorMessage(err: unknown, fallback: string): string {
  if (err instanceof ZeeqApiError) {
    return err.message || fallback;
  }
  return err instanceof Error ? err.message : fallback;
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useCredentialsStore, import.meta.hot));
}
