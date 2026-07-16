const activationReturnPaths = ["/activate-organization", "/activate-account"];

/** Activation URLs must never be preserved as post-login targets. */
export function isActivationReturnUrl(returnUrl: unknown): boolean {
  if (typeof returnUrl !== "string") {
    return false;
  }

  try {
    const parsed = new URL(returnUrl, window.location.origin);
    const basePath = new URL(import.meta.env.BASE_URL, window.location.origin)
      .pathname.replace(/\/$/, "");
    const localPath =
      basePath && parsed.pathname.startsWith(`${basePath}/`)
        ? parsed.pathname.slice(basePath.length)
        : parsed.pathname;

    if (
      localPath === "/login" &&
      parsed.searchParams.get("inactiveOrg") === "true"
    ) {
      return true;
    }

    return activationReturnPaths.some(
      (path) => localPath === path || localPath.startsWith(`${path}/`),
    );
  } catch {
    return false;
  }
}
