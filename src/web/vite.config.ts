import type { ProxyOptions } from "vite";
import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";
import ui from "@nuxt/ui/vite";
import path from "path";
import { VitePWA } from "vite-plugin-pwa";

import VueRouter from "unplugin-vue-router/vite";
import { VueUseComponentsResolver } from "unplugin-vue-components/resolvers";

const viteBase = process.env.VITE_BASE || "/";

const manifestAssetPath = (fileName: string) =>
  `${viteBase.replace(/\/?$/, "/")}${fileName}`;

/** Shared proxy config for routes that forward to zeeq-server. */
function backendProxy(): ProxyOptions {
  return {
    target: process.env.VITE_BACKEND_URL || "http://localhost:5025",
    changeOrigin: false,
    secure: false,
  };
}

// https://vite.dev/config/
export default defineConfig({
  base: viteBase,
  plugins: [
    vue(),
    ui({
      theme: {
        // Adds `tertiary` (purple) alongside the defaults so severity badges
        // (e.g. code review "Comment" findings) can use a color distinct from
        // `secondary`/`info`, which both default to blue.
        colors: [
          "primary",
          "secondary",
          "tertiary",
          "info",
          "success",
          "warning",
          "error",
        ],
      },
      ui: {
        colors: {
          primary: "cyan",
          neutral: "zinc",
          tertiary: "purple",
        },
        button: {
          // Neutral solid buttons use subtle variant styling in dark mode.
          compoundVariants: [
            {
              color: "neutral",
              variant: "solid",
              class:
                "dark:ring dark:ring-inset dark:ring-accented dark:text-default dark:bg-elevated dark:hover:bg-accented/75 dark:active:bg-accented/75",
            },
          ],
        },
        icons: {
          arrowDown: "i-hugeicons-arrow-down-01",
          arrowLeft: "i-hugeicons-arrow-left-01",
          arrowRight: "i-hugeicons-arrow-right-01",
          arrowUp: "i-hugeicons-arrow-up-01",
          caution: "i-hugeicons-alert-02",
          check: "i-hugeicons-tick-02",
          chevronDoubleLeft: "i-hugeicons-arrow-left-double",
          chevronDoubleRight: "i-hugeicons-arrow-right-double",
          chevronDown: "i-hugeicons-arrow-down-01",
          chevronLeft: "i-hugeicons-arrow-left-01",
          chevronRight: "i-hugeicons-arrow-right-01",
          chevronUp: "i-hugeicons-arrow-up-01",
          close: "i-hugeicons-cancel-01",
          copy: "i-hugeicons-copy-01",
          copyCheck: "i-hugeicons-copy-check",
          dark: "i-hugeicons-moon-02",
          drag: "i-hugeicons-drag-drop-vertical",
          ellipsis: "i-hugeicons-more-horizontal",
          error: "i-hugeicons-cancel-circle",
          external: "i-hugeicons-link-square-01",
          eye: "i-hugeicons-view",
          eyeOff: "i-hugeicons-view-off",
          file: "i-hugeicons-file-01",
          folder: "i-hugeicons-folder-01",
          folderOpen: "i-hugeicons-folder-open",
          hash: "i-hugeicons-hashtag",
          info: "i-hugeicons-information-circle",
          light: "i-hugeicons-sun-03",
          loading: "i-hugeicons-loading-03",
          menu: "i-hugeicons-menu-01",
          minus: "i-hugeicons-minus-sign",
          panelClose: "i-hugeicons-layout-left",
          panelOpen: "i-hugeicons-sidebar-left",
          plus: "i-hugeicons-plus-sign",
          reload: "i-hugeicons-reload",
          search: "i-hugeicons-search-01",
          stop: "i-hugeicons-square",
          success: "i-hugeicons-checkmark-circle-02",
          system: "i-hugeicons-computer",
          tip: "i-hugeicons-bulb",
          upload: "i-hugeicons-upload-01",
          warning: "i-hugeicons-alert-01",
        },
      },
      autoImport: {
        resolvers: [VueUseComponentsResolver()],
        imports: ["vue", "pinia", "vue-router", "@vueuse/core"],
      },
    }),
    VueRouter(),
    VitePWA({
      registerType: "prompt",
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg,png,woff2}"],
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
      },
      manifest: {
        name: "Zeeq",
        short_name: "Zeeq",
        theme_color: "#ffffff",
        icons: [
          {
            src: manifestAssetPath("android-chrome-192x192.png"),
            sizes: "192x192",
            type: "image/png",
          },
          {
            src: manifestAssetPath("android-chrome-512x512.png"),
            sizes: "512x512",
            type: "image/png",
          },
        ],
      },
    }),
  ],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"), // Alias '@' to the 'src' directory
    },
  },
  server: {
    // Yarp fails without this.
    allowedHosts: true,
    proxy: {
      "/api": backendProxy(),
      "/health": backendProxy(),
      "/healthcheck": backendProxy(),
      "/auth/providers": backendProxy(),
      "/auth/login/": backendProxy(),
      "/auth/callback/": backendProxy(),
      "/auth/complete/": backendProxy(),
      "/auth/logout": backendProxy(),
      // OpenIddict endpoints (authorize, token, register, well-known) must reach
      // the backend so the Vue origin (zeeq-web.localhost:8095) can serve the
      // full interactive auth flow on one origin with the identity cookie.
      "/connect": backendProxy(),
      "/.well-known": backendProxy(),
      // MCP streamable-http endpoint. Proxied so the MCP server URL matches the
      // Auth:Resource (http://zeeq-web.localhost:8095/mcp) and lives on the
      // same origin as the auth server. ws:true keeps the SSE stream open.
      "/mcp": {
        ...backendProxy(),
        ws: true,
      },
    },
  },
  test: {
    environment: "happy-dom",
    globals: false,
    include: ["src/**/*.test.ts"],
    restoreMocks: true,
    clearMocks: true,
  },
});
