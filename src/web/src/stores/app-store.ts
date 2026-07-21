import { defineStore, acceptHMRUpdate } from "pinia";
import {
  Auth,
  Invitations,
  Memberships,
  type MeResponse,
  type OrgSummary,
} from "@/api/generated";
import type { InvitationResponse } from "@/api/generated/types/InvitationResponse";
import type { SameDomainInvitationDetailsResponse } from "@/api/generated/types/SameDomainInvitationDetailsResponse";
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

const sameDomainInvitationSuppressionKey =
  "zeeq.sameDomainInvitation.dismissedIds";

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
  const sameDomainInvitation = ref<SameDomainInvitationDetailsResponse | null>(
    null,
  );
  const sameDomainInvitationLoading = ref(false);
  const sameDomainInvitationError = ref<string | null>(null);
  const sameDomainInvitationChecked = ref(false);
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
  let fetchSameDomainInvitationPromise: Promise<void> | null = null;
  let fetchSameDomainInvitationAllowsUnauthenticated = false;
  let fetchUserRequestId = 0;
  let fetchBackendVersionRequestId = 0;
  let fetchSameDomainInvitationRequestId = 0;

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
            err instanceof Error
              ? err.message
              : "Could not load backend version.";
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

  /**
   * Finds the first pending same-domain invitation that has not been dismissed
   * for the current browser session.
   *
   * `allowWithoutAuthenticated` is only for the first-login inactive-org path:
   * /me can redirect to /login?inactiveOrg=true before the user accepts their
   * same-domain invitation, while invitation endpoints can still use the
   * signed browser cookie to prove the caller.
   */
  async function fetchSameDomainInvitation(
    options: { force?: boolean; allowWithoutAuthenticated?: boolean } = {},
  ) {
    const allowWithoutAuthenticated =
      options.allowWithoutAuthenticated === true;
    if (!isAuthenticated.value && !allowWithoutAuthenticated) {
      // NOTE: Authenticated-only callers can resolve synchronously while logged
      // out. Avoid caching a no-op promise that can race the inactive-login
      // lookup, which intentionally allows cookie-authenticated invitation APIs.
      sameDomainInvitation.value = null;
      sameDomainInvitationChecked.value = true;
      sameDomainInvitationLoading.value = false;
      return;
    }

    if (
      fetchSameDomainInvitationPromise &&
      !options.force &&
      fetchSameDomainInvitationAllowsUnauthenticated ===
        allowWithoutAuthenticated
    ) {
      return fetchSameDomainInvitationPromise;
    }

    const requestId = ++fetchSameDomainInvitationRequestId;
    fetchSameDomainInvitationAllowsUnauthenticated = allowWithoutAuthenticated;
    // Defer one microtask so the shared promise is assigned before a synchronous
    // auth-state branch can complete and run the cleanup path.
    fetchSameDomainInvitationPromise = Promise.resolve().then(() =>
      runSameDomainInvitationFetch(requestId, allowWithoutAuthenticated),
    );

    return fetchSameDomainInvitationPromise;
  }

  async function runSameDomainInvitationFetch(
    requestId: number,
    allowWithoutAuthenticated: boolean,
  ) {
    try {
      if (requestId === fetchSameDomainInvitationRequestId) {
        sameDomainInvitationLoading.value = true;
        sameDomainInvitationError.value = null;
      }

      const invitations = await Invitations.listInvitations();
      const suppressedIds = readSuppressedSameDomainInvitationIds();
      const result = await findSameDomainInvitation(invitations, suppressedIds);

      if (requestId === fetchSameDomainInvitationRequestId) {
        const isSuppressed =
          result !== null &&
          readSuppressedSameDomainInvitationIds().has(result.invitationId);

        if (result !== null && !isSuppressed) {
          sameDomainInvitation.value = result;
        } else {
          sameDomainInvitation.value = null;
        }
        sameDomainInvitationChecked.value = true;
      }
    } catch (err: unknown) {
      if (
        allowWithoutAuthenticated &&
        err instanceof ZeeqApiError &&
        err.status === 401
      ) {
        if (requestId === fetchSameDomainInvitationRequestId) {
          sameDomainInvitation.value = null;
          sameDomainInvitationChecked.value = true;
        }
        return;
      }

      if (requestId === fetchSameDomainInvitationRequestId) {
        sameDomainInvitationError.value =
          err instanceof Error
            ? err.message
            : "Could not load same-domain invitation.";
      }
      throw err;
    } finally {
      if (requestId === fetchSameDomainInvitationRequestId) {
        sameDomainInvitationLoading.value = false;
        fetchSameDomainInvitationPromise = null;
        fetchSameDomainInvitationAllowsUnauthenticated = false;
      }
    }
  }

  /** Suppresses a same-domain invitation prompt for the current browser session. */
  function suppressSameDomainInvitation(invitationId: string) {
    const suppressedIds = readSuppressedSameDomainInvitationIds();
    suppressedIds.add(invitationId);
    writeSuppressedSameDomainInvitationIds(suppressedIds);

    if (sameDomainInvitation.value?.invitationId === invitationId) {
      sameDomainInvitation.value = null;
    }
  }

  /** POSTs /auth/logout to clear the cookie, then resets local state. */
  async function logout() {
    await fetch("/auth/logout", { method: "POST" });
    user.value = null;
    isAuthenticated.value = false;
    authChecked.value = false;
    authError.value = null;
    fetchSameDomainInvitationRequestId++;
    fetchSameDomainInvitationPromise = null;
    fetchSameDomainInvitationAllowsUnauthenticated = false;
    sameDomainInvitation.value = null;
    sameDomainInvitationLoading.value = false;
    sameDomainInvitationChecked.value = false;
    sameDomainInvitationError.value = null;
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
   * Accepts a same-domain invitation, makes that organization default, switches
   * the active org cookie, and refreshes /me without forcing a full reload.
   */
  async function acceptSameDomainInvitationAsDefault(invitationId: string) {
    invitationSaving.value = true;
    authError.value = null;
    sameDomainInvitationError.value = null;

    try {
      const details =
        sameDomainInvitation.value?.invitationId === invitationId
          ? sameDomainInvitation.value
          : await Invitations.getSameDomainInvitationDetails(invitationId);

      await Invitations.acceptInvitationAsDefault(invitationId);
      await Memberships.switchOrganization(details.organizationId);
      await fetchUser({ force: true });

      sameDomainInvitation.value = null;
      return details.organizationId;
    } catch (err: unknown) {
      const message =
        err instanceof Error
          ? err.message
          : "Could not accept same-domain invitation.";
      authError.value = message;
      sameDomainInvitationError.value = message;
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
    sameDomainInvitation,
    sameDomainInvitationLoading,
    sameDomainInvitationError,
    sameDomainInvitationChecked,
    authChecked,
    isSystemAdmin,
    currentOrganization,
    fetchUser,
    fetchBackendVersion,
    fetchSameDomainInvitation,
    switchOrganization,
    acceptInvitation,
    acceptSameDomainInvitationAsDefault,
    declineInvitation,
    suppressSameDomainInvitation,
    logout,
  };
});

async function findSameDomainInvitation(
  invitations: InvitationResponse[],
  suppressedIds: Set<string>,
): Promise<SameDomainInvitationDetailsResponse | null> {
  for (const invitation of invitations) {
    if (suppressedIds.has(invitation.id)) {
      continue;
    }

    try {
      return await Invitations.getSameDomainInvitationDetails(invitation.id);
    } catch (err: unknown) {
      if (err instanceof ZeeqApiError && err.status === 404) {
        continue;
      }

      throw err;
    }
  }

  return null;
}

function readSuppressedSameDomainInvitationIds(): Set<string> {
  if (typeof sessionStorage === "undefined") {
    return new Set();
  }

  const raw = sessionStorage.getItem(sameDomainInvitationSuppressionKey);
  if (!raw) {
    return new Set();
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return new Set();
    }

    return new Set(
      parsed.filter((item): item is string => typeof item === "string"),
    );
  } catch {
    return new Set();
  }
}

function writeSuppressedSameDomainInvitationIds(ids: Set<string>): void {
  if (typeof sessionStorage === "undefined") {
    return;
  }

  sessionStorage.setItem(
    sameDomainInvitationSuppressionKey,
    JSON.stringify([...ids]),
  );
}

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
