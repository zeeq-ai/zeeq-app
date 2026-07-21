import { createRouter, createWebHistory } from "vue-router";
import { routes } from "./routes";
import { isActivationReturnUrl } from "./return-url";
import { useAppStore } from "@/stores/app-store";

const router = createRouter({
  // import.meta.env.BASE_URL is set by Vite from the `base` config option
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
});

/**
 * Auth guard.  Calls fetchUser() on first navigation.  Authenticated users
 * are redirected away from /login; unauthenticated users are sent to /login
 * with ?returnUrl= unless the route is public (/login, /auth/login, /auth/complete,
 * /login?inactiveOrg=true).
 */
router.beforeEach(async (to) => {
  const store = useAppStore();
  const loginReturnUrl = readSingleQueryValue(to.query.returnUrl);
  const isLoginRoute = to.path === "/login" || to.path === "/auth/login";
  const isJoinYourTeamRoute = to.path === "/join-your-team";
  const isInactiveOrgLoginRoute =
    isLoginRoute && readSingleQueryValue(to.query.inactiveOrg) === "true";
  if (isLoginRoute && isActivationReturnUrl(loginReturnUrl)) {
    const query = { ...to.query };
    delete query.returnUrl;

    return { path: to.path, query, replace: true };
  }

  // Re-check auth on first load AND when arriving on auth entry routes
  // (/login, /auth/login, /auth/complete, /select-org) while not yet authenticated.  Without
  // this, a successful OAuth login that redirects back to /login?returnUrl=...
  // would never observe the newly-issued session cookie because authChecked was
  // already set true on the first anonymous visit.  /select-org is included
  // because the DCR authorize endpoint redirects to it with a fresh cookie.
  const isAuthEntryRoute =
    to.path.startsWith("/login") ||
    to.path.startsWith("/auth/login") ||
    to.path.startsWith("/auth/complete") ||
    to.path.startsWith("/select-org");
  if (
    !isInactiveOrgLoginRoute &&
    !store.isAuthenticated &&
    (!store.authChecked || isAuthEntryRoute)
  ) {
    await store.fetchUser();
  }

  const isPublicRoute =
    isInactiveOrgLoginRoute ||
    to.path === "/login" ||
    to.path === "/auth/login" ||
    to.path.startsWith("/login?") ||
    to.path.startsWith("/auth/login?") ||
    to.path.startsWith("/auth/complete");

  if (store.isAuthenticated) {
    if (requiresSystemAdmin(to) && !store.isSystemAdmin) {
      return { path: "/" };
    }

    if (to.path.startsWith("/login") || to.path.startsWith("/auth/login")) {
      if (isInactiveOrgLoginRoute) {
        return true;
      }

      const returnUrl = readSingleQueryValue(to.query.returnUrl);
      if (!returnUrl) {
        return { path: "/" };
      }
      // Absolute URLs come from the OAuth round-trip (Login.vue resolves
      // relative returnUrl to absolute for backend handoff detection).
      // Vue Router's `path` expects an internal route, not a full URL.
      if (/^https?:\/\//i.test(returnUrl)) {
        window.location.href = returnUrl;
        return false;
      }
      return { path: returnUrl };
    }

    if (
      !isJoinYourTeamRoute &&
      !isAuthEntryRoute &&
      (await hasSameDomainInvitation(store))
    ) {
      return {
        path: "/join-your-team",
        query: { returnUrl: to.fullPath },
      };
    }

    return true;
  }

  if (!isPublicRoute) {
    return {
      path: "/login",
      query: { returnUrl: to.fullPath },
    };
  }

  return true;
});

export default router;

/** True when any matched route segment is system-admin-only. */
function requiresSystemAdmin(to: {
  matched: Array<{ meta: Record<string, unknown> }>;
}) {
  return to.matched.some((route) => route.meta.requiresSystemAdmin === true);
}

/** Reads a query value only when it is present as a single string. */
function readSingleQueryValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

async function hasSameDomainInvitation(store: ReturnType<typeof useAppStore>) {
  try {
    await store.fetchSameDomainInvitation();
    return store.sameDomainInvitation !== null;
  } catch {
    return false;
  }
}
