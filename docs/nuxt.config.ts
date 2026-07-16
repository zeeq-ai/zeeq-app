export default defineNuxtConfig({
  extends: ["docus"],
  compatibilityDate: "2025-01-01",
  content: {
    build: {
      markdown: {
        highlight: {
          // Docus provides a small default Shiki language set. Keep those defaults
          // and add the languages used by the Zeeq architecture docs.
          langs: [
            "bash",
            "diff",
            "json",
            "jsonc",
            "js",
            "ts",
            "html",
            "css",
            "vue",
            "shell",
            "mdc",
            "md",
            "yaml",
            "csharp",
          ],
        },
      },
    },
  },
  robots: { robotsTxt: false },
  routeRules: {
    // Pre-render all pages at build time
    "**": { prerender: true },
  },
  vite: {
    server: {
      allowedHosts: true,
    },
  },
});
