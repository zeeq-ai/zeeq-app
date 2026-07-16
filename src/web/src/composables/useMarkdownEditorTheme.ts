/**
 * Resolves the md-editor-v3 `theme` ("light" | "dark") from the app color mode,
 * with a manual per-session override toggle so authors can flip the editor
 * independent of the shell.
 */
export function useMarkdownEditorTheme() {
  const colorMode = useColorMode();

  /** Manual override: null means follow the app color mode. */
  const override = ref<"light" | "dark" | null>(null);

  const editorTheme = computed<"light" | "dark">(
    () => override.value ?? (colorMode.value === "dark" ? "dark" : "light"),
  );

  /** Toggles between light and dark, breaking away from app color mode. */
  function toggleTheme() {
    override.value = editorTheme.value === "dark" ? "light" : "dark";
  }

  return { editorTheme, toggleTheme };
}
