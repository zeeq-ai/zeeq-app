import type {
  Client,
  RequestConfig,
  ResponseConfig,
  ResponseErrorConfig,
} from "@kubb/plugin-client/clients/fetch";
import { mergeConfig } from "@kubb/plugin-client/clients/fetch";

let globalConfig: Partial<RequestConfig<unknown>> = {};

/** Returns process-wide defaults shared by generated Kubb clients. */
export function getConfig(): Partial<RequestConfig<unknown>> {
  return globalConfig;
}

/**
 * Replaces process-wide defaults shared by generated Kubb clients.
 *
 * @param config - Request defaults to apply before per-operation config.
 * @return The updated defaults.
 */
export function setConfig(
  config: Partial<RequestConfig>,
): Partial<RequestConfig<unknown>> {
  globalConfig = config;
  return globalConfig;
}

export { mergeConfig };
export type { Client, RequestConfig, ResponseConfig, ResponseErrorConfig };

/** Error thrown by generated API calls when the HTTP response is not successful. */
export class ZeeqApiError extends Error {
  readonly status: number;
  readonly statusText: string;
  readonly data: unknown;

  constructor(status: number, statusText: string, data: unknown) {
    super(readErrorMessage(status, statusText, data));
    this.name = "ZeeqApiError";
    this.status = status;
    this.statusText = statusText;
    this.data = data;
  }
}

/**
 * Fetch transport used by Kubb-generated clients.
 *
 * It keeps generated clients same-origin by default, includes cookies for
 * browser auth, sets JSON headers for JSON bodies, and throws status-aware
 * errors so stores can distinguish unauthenticated users from transient API
 * failures.
 */
const client: Client = async <
  TResponseData,
  _TError = unknown,
  TRequestData = unknown,
>(
  paramsConfig: RequestConfig<TRequestData>,
): Promise<ResponseConfig<TResponseData>> => {
  const config = mergeConfig(getConfig(), paramsConfig);
  const targetUrl = buildUrl(config);
  const headers = buildHeaders(config);
  const response = await fetch(targetUrl, {
    credentials: config.credentials || "same-origin",
    method: config.method?.toUpperCase(),
    body: buildBody(config.data),
    signal: config.signal,
    headers,
  });

  redirectToInactiveOrgActivation(response);

  const data = await readResponseData<TResponseData>(
    response,
    config.responseType,
  );

  if (!response.ok) {
    throw new ZeeqApiError(response.status, response.statusText, data);
  }

  return {
    data,
    status: response.status,
    statusText: response.statusText,
    headers: response.headers,
  };
};

export default client;

/** Redirects browser callers before an inactive-org response is parsed as JSON. */
function redirectToInactiveOrgActivation(response: Response): void {
  if (!response.redirected) {
    return;
  }

  const redirectUrl = new URL(
    response.url,
    globalThis.location?.origin ?? "http://localhost",
  );
  if (!isInactiveOrgLoginRedirect(redirectUrl)) {
    return;
  }

  if (
    globalThis.location?.pathname !== redirectUrl.pathname ||
    globalThis.location?.search !== redirectUrl.search
  ) {
    globalThis.location?.assign(redirectUrl.href);
  }

  throw new ZeeqApiError(response.status, response.statusText, {
    detail: "Organization activation is required.",
  });
}

/** True for the login-page state used when the active organization is inactive. */
function isInactiveOrgLoginRedirect(redirectUrl: URL): boolean {
  const basePath = new URL(
    import.meta.env.BASE_URL,
    redirectUrl.origin,
  ).pathname.replace(/\/$/, "");
  const localPath =
    basePath && redirectUrl.pathname.startsWith(`${basePath}/`)
      ? redirectUrl.pathname.slice(basePath.length)
      : redirectUrl.pathname;

  return (
    localPath === "/login" &&
    redirectUrl.searchParams.get("inactiveOrg") === "true"
  );
}

/** Builds the final request URL from a generated path plus optional params. */
function buildUrl(config: Partial<RequestConfig>): string {
  const normalizedParams = new URLSearchParams();

  if (config.params && typeof config.params === "object") {
    for (const [key, value] of Object.entries(config.params)) {
      if (value === undefined) {
        continue;
      }
      // Minimal API binds array query params (e.g. string[]) from repeated
      // `key=a&key=b` entries, not one comma-joined value — appending a
      // single "a,b" value binds server-side as a one-element array
      // containing a literal comma, which then matches nothing.
      if (Array.isArray(value)) {
        for (const item of value) {
          normalizedParams.append(key, queryParamValue(item));
        }
      } else {
        normalizedParams.append(key, queryParamValue(value));
      }
    }
  }

  let targetUrl = [config.baseURL, config.url].filter(Boolean).join("");
  const query = normalizedParams.toString();

  if (query) {
    targetUrl += `?${query}`;
  }

  return targetUrl;
}

/** Serializes query params in the format Minimal APIs expect. */
function queryParamValue(value: unknown): string {
  if (value === null) {
    return "null";
  }

  if (value instanceof Date) {
    return value.toISOString();
  }

  return String(value);
}

/** Adds JSON headers for generated calls with JSON request bodies. */
function buildHeaders(config: Partial<RequestConfig>): Headers {
  const headers = new Headers(config.headers);

  if (
    config.data !== undefined &&
    !(config.data instanceof FormData) &&
    !headers.has("Content-Type")
  ) {
    headers.set("Content-Type", "application/json");
  }

  return headers;
}

/** Serializes generated request data into a fetch body. */
function buildBody(data: unknown): BodyInit | undefined {
  if (data === undefined) {
    return undefined;
  }

  if (data instanceof FormData) {
    return data;
  }

  return JSON.stringify(data);
}

/** Reads JSON, text, and empty API responses without losing HTTP status. */
async function readResponseData<TResponseData>(
  response: Response,
  responseType: RequestConfig["responseType"],
): Promise<TResponseData> {
  if ([204, 205, 304].includes(response.status)) {
    return {} as TResponseData;
  }

  const text = await response.text();
  if (!text) {
    return {} as TResponseData;
  }

  if (responseType === "text") {
    return text as TResponseData;
  }

  return JSON.parse(text) as TResponseData;
}

/** Extracts a friendly message from ProblemDetails-style API errors. */
function readErrorMessage(
  status: number,
  statusText: string,
  data: unknown,
): string {
  if (data && typeof data === "object") {
    const problem = data as {
      title?: string;
      detail?: string;
      errors?: Record<string, string[]>;
    };
    const validationMessages = problem.errors
      ? Object.values(problem.errors).flat()
      : [];

    return (
      problem.detail ??
      validationMessages[0] ??
      problem.title ??
      `Request failed: ${status}`
    );
  }

  return statusText || `Request failed: ${status}`;
}
