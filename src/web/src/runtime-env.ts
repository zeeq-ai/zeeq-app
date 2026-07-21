/**
 * Runtime environment configuration.
 *
 * Same-origin API calls are the default. Vite environment variables can
 * override these values only for deployments that intentionally call a
 * separate API origin.
 */

/**
 * Optional base URL used by the custom Kubb HTTP client.
 */
export const apiBaseUrl =
  import.meta.env.VITE_API_BASE_URL?.trim() || undefined;

/**
 * External documentation entry point used by the shared application shell.
 */
export const docsUrl =
  import.meta.env.VITE_DOCS_URL?.trim() ||
  "https://zeeq.ai/docs/getting-started/introduction";

/**
 * True when Vite is serving the app in development mode.
 */
export const isDevelopment = import.meta.env.MODE === "development";
