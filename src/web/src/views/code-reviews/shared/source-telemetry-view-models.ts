import type { CodeReviewSourceTelemetryDto } from "@/api/generated";

export type SourceTelemetryAccordionValue =
  "documents" | "snippets" | "tools" | "content-gaps";

export type SourceTelemetryAccordionItem = {
  label: string;
  value: SourceTelemetryAccordionValue;
  icon: string;
  count: number;
};

export type SourceTelemetryDocumentRow = {
  value: string;
  label: string;
  description: string;
  icon: string;
  iconClass: string;
  isTop: boolean;
  readAfterSearch: boolean;
  snippetCountLabel?: string;
  facets: string[];
};

export type SourceTelemetrySnippetRow = {
  value: string;
  label: string;
  description: string;
  icon: string;
  iconClass: string;
  isTop: boolean;
  kindLabel: string;
  languageLabel: string | null;
  facets: string[];
};

export type SourceTelemetryToolRow = {
  value: string;
  label: string;
  calls: number;
  succeeded: number;
  failed: number;
};

export type SourceTelemetryContentGapRow = {
  value: string;
  query: string;
  tool: string;
  facets: string[];
};

/**
 * Builds top-level accordion sections from populated telemetry groups. Empty
 * groups are omitted so persisted reviews and synthetic tests stay compact.
 */
export function buildSourceTelemetryAccordionItems(
  sourceTelemetry: CodeReviewSourceTelemetryDto | null | undefined,
): SourceTelemetryAccordionItem[] {
  const documents = buildSourceTelemetryDocumentRows(sourceTelemetry);
  const snippets = buildSourceTelemetrySnippetRows(sourceTelemetry);
  const tools = buildSourceTelemetryToolRows(sourceTelemetry);
  const contentGaps = buildSourceTelemetryContentGapRows(sourceTelemetry);

  const items: SourceTelemetryAccordionItem[] = [
    {
      label: "Documents",
      value: "documents",
      icon: "i-hugeicons-book-01",
      count: documents.length,
    },
    {
      label: "Sections and snippets",
      value: "snippets",
      icon: "i-hugeicons-paragraph",
      count: snippets.length,
    },
    {
      label: "Tools",
      value: "tools",
      icon: "i-hugeicons-wrench-01",
      count: tools.length,
    },
    {
      label: "Content gaps",
      value: "content-gaps",
      icon: "i-hugeicons-search-01",
      count: contentGaps.length,
    },
  ];

  return items.filter((item) => item.count > 0);
}

export function buildSourceTelemetryDocumentRows(
  sourceTelemetry: CodeReviewSourceTelemetryDto | null | undefined,
): SourceTelemetryDocumentRow[] {
  return (sourceTelemetry?.documents ?? []).map((document, index) => {
    const snippetCount = document.snippets?.length ?? 0;

    return {
      value: `document:${document.documentId || document.path}:${index}`,
      label: document.title || document.path,
      description: `${document.library} · ${document.path}`,
      icon: document.readAfterSearch
        ? "i-hugeicons-book-open-01"
        : "i-hugeicons-book-01",
      iconClass: document.readAfterSearch ? "text-primary" : "text-muted",
      isTop: toNumber(document.bestRank) === 1,
      readAfterSearch: document.readAfterSearch,
      snippetCountLabel:
        snippetCount > 0 ? `${snippetCount} snippets` : undefined,
      facets: document.facets ?? [],
    };
  });
}

export function buildSourceTelemetrySnippetRows(
  sourceTelemetry: CodeReviewSourceTelemetryDto | null | undefined,
): SourceTelemetrySnippetRow[] {
  return (sourceTelemetry?.documents ?? []).flatMap((document, documentIndex) =>
    (document.snippets ?? []).map((snippet, snippetIndex) => ({
      value: `snippet:${document.documentId || document.path}:${snippet.snippetId || snippet.heading}:${documentIndex}:${snippetIndex}`,
      label: snippet.heading,
      description: `${document.path}${snippet.language ? ` · ${snippet.language}` : ""}`,
      icon:
        snippet.kind === "CodeSample"
          ? "i-hugeicons-code"
          : "i-hugeicons-paragraph",
      iconClass: snippet.kind === "CodeSample" ? "text-primary" : "text-muted",
      isTop: toNumber(snippet.bestRank) === 1,
      kindLabel: snippet.kind,
      languageLabel: snippet.language,
      facets: snippet.facets ?? [],
    })),
  );
}

export function buildSourceTelemetryToolRows(
  sourceTelemetry: CodeReviewSourceTelemetryDto | null | undefined,
): SourceTelemetryToolRow[] {
  return (sourceTelemetry?.toolUsage ?? []).map((tool) => ({
    value: tool.tool,
    label: tool.tool,
    calls: toNumber(tool.calls),
    succeeded: toNumber(tool.succeeded),
    failed: toNumber(tool.failed),
  }));
}

export function buildSourceTelemetryContentGapRows(
  sourceTelemetry: CodeReviewSourceTelemetryDto | null | undefined,
): SourceTelemetryContentGapRow[] {
  return (sourceTelemetry?.missedQueries ?? []).map((missedQuery, index) => ({
    value: `content-gap:${missedQuery.tool}:${missedQuery.query}:${index}`,
    query: missedQuery.query,
    tool: missedQuery.tool,
    facets: missedQuery.facets ?? [],
  }));
}

function toNumber(value: number | string | null | undefined): number {
  const numeric = Number(value ?? 0);
  return Number.isFinite(numeric) ? numeric : 0;
}
