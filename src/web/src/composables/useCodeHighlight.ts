import highlight from "@comark/vue/plugins/highlight";
import type { LanguageRegistration } from "shiki";
import csharp from "shiki/dist/langs/csharp.mjs";
import dart from "shiki/dist/langs/dart.mjs";
import dockerfile from "shiki/dist/langs/dockerfile.mjs";
import elixir from "shiki/dist/langs/elixir.mjs";
import fsharp from "shiki/dist/langs/fsharp.mjs";
import go from "shiki/dist/langs/go.mjs";
import ini from "shiki/dist/langs/ini.mjs";
import java from "shiki/dist/langs/java.mjs";
import kotlin from "shiki/dist/langs/kotlin.mjs";
import perl from "shiki/dist/langs/perl.mjs";
import php from "shiki/dist/langs/php.mjs";
import powershell from "shiki/dist/langs/powershell.mjs";
import python from "shiki/dist/langs/python.mjs";
import ruby from "shiki/dist/langs/ruby.mjs";
import rust from "shiki/dist/langs/rust.mjs";
import scala from "shiki/dist/langs/scala.mjs";
import scss from "shiki/dist/langs/scss.mjs";
import sql from "shiki/dist/langs/sql.mjs";
import swift from "shiki/dist/langs/swift.mjs";
import toml from "shiki/dist/langs/toml.mjs";
import xml from "shiki/dist/langs/xml.mjs";

/**
 * Extends Comark's default code fence languages with common backend, systems, scripting, and
 * data languages this repo's own code snippet corpus is likely to contain — shared by every view
 * that renders highlighted code snippets (Test search, parse preview, PR code review bodies).
 */
const additionalHighlightLanguages: (
  | LanguageRegistration
  | LanguageRegistration[]
)[] = [
  csharp,
  dart,
  dockerfile,
  elixir,
  fsharp,
  go,
  ini,
  java,
  kotlin,
  perl,
  php,
  powershell,
  python,
  ruby,
  rust,
  scala,
  scss,
  sql,
  swift,
  toml,
  xml,
];

const codeHighlightPlugins = [
  highlight({ languages: additionalHighlightLanguages }),
];

/**
 * Picks a backtick fence at least one character longer than the longest run of backticks in
 * `content`, so a snippet that itself contains a fenced code block cannot prematurely close the
 * wrapping fence — mirrors the backend's MCP markdown formatter (DocumentLibraryMcpTools.CodeFence).
 */
function codeFence(content: string): string {
  let longestRun = 0;
  let currentRun = 0;

  for (const character of content) {
    currentRun = character === "`" ? currentRun + 1 : 0;
    longestRun = Math.max(longestRun, currentRun);
  }

  return "`".repeat(Math.max(3, longestRun + 1));
}

/** Code-fence rendering shared by every view that previews highlighted code snippets. */
export function useCodeHighlight() {
  /** Wraps code content in a fence Comark can render with syntax highlighting. */
  function toFencedMarkdown(content: string, language?: string | null): string {
    const fence = codeFence(content);

    return `${fence}${language ?? ""}\n${content}\n${fence}`;
  }

  return { codeHighlightPlugins, toFencedMarkdown };
}
