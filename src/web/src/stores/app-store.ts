import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Auth,
  Invitations,
  Memberships,
  type MeResponse,
  type OrgSummary,
} from "@/api/generated";
import { ZeeqApiError } from "@/api/zeeq-api-client";

type BackendVersionInfo = {
  status: string;
  checkedAtUtc: string;
  sha?: string | null;
  buildTimeEst?: string | null;
  version?: string | null;
  versionTag?: string | null;
  displayVersion: string;
};

/**
 * Global app store.  Auth state via the generated GET /api/v1/me client.
 * The HttpOnly zeeq_identity_session cookie is never read directly; a 401 means unauthenticated.
 */
export const useAppStore = defineStore("app-store", () => {
  const user = ref<MeResponse | null>(null);
  const backendVersion = ref<BackendVersionInfo | null>(null);
  const backendVersionError = ref<string | null>(null);
  const isAuthenticated = ref(false);
  const authLoading = ref(true);
  const authError = ref<string | null>(null);
  const invitationSaving = ref(false);
  /** Set after the first fetchUser() call completes. */
  const authChecked = ref(false);
  const isSystemAdmin = computed<boolean>(
    () => user.value?.isSystemAdmin === true,
  );

  /**
   * The active organization is cookie-backed on the server.  Keep this derived
   * from /me so sidebar, routes, and settings views all agree after switch-org.
   */
  const currentOrganization = computed<OrgSummary | null>(() => {
    const orgs = user.value?.organizations ?? [];
    const currentOrgId = user.value?.organizationId;

    return orgs.find((org) => org.id === currentOrgId) ?? null;
  });

  let fetchUserPromise: Promise<void> | null = null;
  let fetchBackendVersionPromise: Promise<void> | null = null;
  let fetchUserRequestId = 0;
  let fetchBackendVersionRequestId = 0;

  /**
   * Calls GET /api/v1/me to determine auth state.
   *
   * The Kubb transport is configured for same-origin cookie auth and throws
   * `ZeeqApiError` with status metadata, so unauthenticated responses can
   * clear local auth state without treating transient failures as logout.
   */
  async function fetchUser(options: { force?: boolean } = {}) {
    if (fetchUserPromise && !options.force) {
      return fetchUserPromise;
    }

    const requestId = ++fetchUserRequestId;
    const promise = (async () => {
      authLoading.value = true;
      authError.value = null;
      try {
        const data = await Auth.getMe();
        if (requestId === fetchUserRequestId) {
          user.value = data;
          isAuthenticated.value = true;
        }
      } catch (err: unknown) {
        if (requestId === fetchUserRequestId) {
          if (err instanceof ZeeqApiError && err.status === 401) {
            user.value = null;
            isAuthenticated.value = false;
          } else {
            // NOTE: Non-401 failures (500, network error, etc.) intentionally
            // preserve the existing user/isAuthenticated state rather than
            // clearing it.  The HttpOnly session cookie is still valid server-side;
            // clearing client state on a transient backend blip would force a
            // confusing re-login.  The authError ref is available for UI to
            // surface the problem (e.g. a banner) without blocking navigation.
            authError.value =
              err instanceof Error ? err.message : "Unknown auth error";
          }
        }
      } finally {
        if (requestId === fetchUserRequestId) {
          authLoading.value = false;
          authChecked.value = true;
        }
        if (requestId === fetchUserRequestId) {
          fetchUserPromise = null;
        }
      }
    })();

    fetchUserPromise = promise;
    return promise;
  }

  /**
   * Loads backend build provenance from /health. This route is intentionally
   * outside the generated OpenAPI client today because health is also used by
   * platform probes before authentication.
   */
  async function fetchBackendVersion(options: { force?: boolean } = {}) {
    if (fetchBackendVersionPromise && !options.force) {
      return fetchBackendVersionPromise;
    }

    const requestId = ++fetchBackendVersionRequestId;
    const promise = (async () => {
      if (requestId === fetchBackendVersionRequestId) {
        backendVersionError.value = null;
      }

      try {
        // NOTE: /health is intentionally excluded from the generated API client
        // because platform probes and pre-auth diagnostics use it too.
        const response = await fetch("/health", {
          credentials: "same-origin",
          headers: { Accept: "application/json" },
        });

        if (!response.ok) {
          throw new Error(`Health check failed with ${response.status}`);
        }

        const payload: unknown = await response.json();

        if (!isBackendVersionInfo(payload)) {
          throw new Error("Invalid backend health response.");
        }

        if (requestId === fetchBackendVersionRequestId) {
          backendVersion.value = payload;
        }
      } catch (err: unknown) {
        if (requestId === fetchBackendVersionRequestId) {
          backendVersionError.value =
            err instanceof Error ? err.message : "Could not load backend version.";
        }
      } finally {
        if (requestId === fetchBackendVersionRequestId) {
          fetchBackendVersionPromise = null;
        }
      }
    })();

    fetchBackendVersionPromise = promise;
    return promise;
  }

  /** POSTs /auth/logout to clear the cookie, then resets local state. */
  async function logout() {
    await fetch("/auth/logout", { method: "POST" });
    user.value = null;
    isAuthenticated.value = false;
    authChecked.value = false;
    authError.value = null;
  }

  /**
   * Switches the server-side active organization, then reloads the app so every
   * org-scoped view remounts against the cookie-updated /me response.
   */
  async function switchOrganization(orgId: string) {
    authError.value = null;

    await Memberships.switchOrganization(orgId);
    if (typeof window !== "undefined") {
      window.location.reload();
      return;
    }

    await fetchUser({ force: true });
  }

  /**
   * Accepts a pending organization invitation and refreshes /me so the shell
   * immediately shows the new active membership.
   */
  async function acceptInvitation(
    invitationId: string,
    organizationId?: string,
  ) {
    invitationSaving.value = true;
    authError.value = null;

    try {
      await Invitations.acceptInvitation(invitationId);
      if (organizationId) {
        await switchOrganization(organizationId);
      } else {
        await fetchUser({ force: true });
      }
    } catch (err: unknown) {
      authError.value =
        err instanceof Error ? err.message : "Could not accept invitation.";
      throw err;
    } finally {
      invitationSaving.value = false;
    }
  }

  /**
   * Declines a pending invitation and refreshes /me to remove it from the
   * notification list that is derived from the identity response.
   */
  async function declineInvitation(invitationId: string) {
    invitationSaving.value = true;
    authError.value = null;

    try {
      await Invitations.declineInvitation(invitationId);
      await fetchUser({ force: true });
    } catch (err: unknown) {
      authError.value =
        err instanceof Error ? err.message : "Could not decline invitation.";
      throw err;
    } finally {
      invitationSaving.value = false;
    }
  }

  return {
    user,
    backendVersion,
    backendVersionError,
    isAuthenticated,
    authLoading,
    authError,
    invitationSaving,
    authChecked,
    isSystemAdmin,
    currentOrganization,
    fetchUser,
    fetchBackendVersion,
    switchOrganization,
    acceptInvitation,
    declineInvitation,
    logout,
  };
});

function isBackendVersionInfo(value: unknown): value is BackendVersionInfo {
  if (!isRecord(value)) {
    return false;
  }

  return (
    typeof value.status === "string" &&
    typeof value.checkedAtUtc === "string" &&
    typeof value.displayVersion === "string" &&
    isOptionalString(value.sha) &&
    isOptionalString(value.buildTimeEst) &&
    isOptionalString(value.version) &&
    isOptionalString(value.versionTag)
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isOptionalString(value: unknown): value is string | null | undefined {
  return value === undefined || value === null || typeof value === "string";
}

if (import.meta.hot) {
  import.meta.hot.accept(acceptHMRUpdate(useAppStore, import.meta.hot));
}
