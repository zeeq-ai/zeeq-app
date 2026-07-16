import { defineConfig } from "@kubb/core";
import { pluginOas } from "@kubb/plugin-oas";
import { pluginTs } from "@kubb/plugin-ts";
import { pluginClient } from "@kubb/plugin-client";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL?.trim() || undefined;

/**
 * Kubb generation for the Zeeq OpenAPI schema.
 *
 * OpenAPI paths include `/api/v1`, so local browser clients should usually omit
 * `baseURL` and let fetch resolve against the current app origin. Set
 * `VITE_API_BASE_URL` only for a deployment that intentionally calls a separate
 * API origin.
 */
export default defineConfig({
  name: "zeeq-kubb",
  root: ".",
  input: {
    path: "./src/api/zeeq-api.json",
  },
  output: {
    path: "./src/api/generated",
    clean: true,
  },
  plugins: [
    pluginOas(),
    pluginTs({
      output: {
        path: "./types",
      },
      enumType: "asConst",
      dateType: "date",
      unknownType: "unknown",
      optionalType: "questionTokenAndUndefined",
    }),
    pluginClient({
      baseURL: apiBaseUrl,
      contentType: "application/json",
      importPath: "@/api/zeeq-api-client",
      clientType: "staticClass",
    }),
  ],
});
