/**
 * Builds an absolute URL for a route served under the configured Vite app base.
 *
 * - `BASE_URL="/"`, path `/code-reviews` -> `http://zeeq-web.localhost:8085/code-reviews` (local)
 * - `BASE_URL="/web/"`, path `/code-reviews` -> `https://app.zeeq.ai/web/code-reviews` (production)
 */
export function toAbsoluteAppUrl(path: string): string {
  const baseUrl = new URL(import.meta.env.BASE_URL, location.origin);

  if (!baseUrl.pathname.endsWith("/")) {
    baseUrl.pathname = `${baseUrl.pathname}/`;
  }

  return new URL(path.replace(/^\/+/, ""), baseUrl).toString();
}
