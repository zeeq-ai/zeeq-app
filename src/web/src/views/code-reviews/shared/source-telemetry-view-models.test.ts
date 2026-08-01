import { describe, expect, it } from "vitest";
import type { CodeReviewSourceTelemetryDto } from "@/api/generated";

import {
  buildSourceTelemetryAccordionItems,
  buildSourceTelemetryContentGapRows,
  buildSourceTelemetryDocumentRows,
  buildSourceTelemetrySnippetRows,
  buildSourceTelemetryToolRows,
  hasSourceTelemetryContent,
} from "./source-telemetry-view-models";

describe("source telemetry view models", () => {
  it("projects populated telemetry groups into accordion sections", () => {
    expect(buildSourceTelemetryAccordionItems(sourceTelemetry())).toEqual([
      {
        label: "Documents",
        value: "documents",
        icon: "i-hugeicons-book-01",
        count: 1,
      },
      {
        label: "Sections and snippets",
        value: "snippets",
        icon: "i-hugeicons-paragraph",
        count: 1,
      },
      {
        label: "Tools",
        value: "tools",
        icon: "i-hugeicons-wrench-01",
        count: 1,
      },
      {
        label: "Content gaps",
        value: "content-gaps",
        icon: "i-hugeicons-search-01",
        count: 1,
      },
    ]);
  });

  it("projects documents and snippets with top, kind, facets, and read markers", () => {
    const telemetry = sourceTelemetry();

    expect(buildSourceTelemetryDocumentRows(telemetry)[0]).toMatchObject({
      label: "Vue guidance",
      description: "zeeq-app · /frontend/vue.md",
      icon: "i-hugeicons-book-open-01",
      iconClass: "text-primary",
      isTop: true,
      readAfterSearch: true,
      snippetCountLabel: "1 snippets",
      facets: ["Logical"],
    });
    expect(buildSourceTelemetrySnippetRows(telemetry)[0]).toMatchObject({
      label: "Vue guidance > Components",
      description: "/frontend/vue.md · vue",
      icon: "i-hugeicons-code",
      iconClass: "text-primary",
      isTop: true,
      kindLabel: "CodeSample",
      languageLabel: "vue",
      facets: ["Logical"],
    });
  });

  it("projects tools and content gaps", () => {
    const telemetry = sourceTelemetry();

    expect(buildSourceTelemetryToolRows(telemetry)[0]).toMatchObject({
      label: "search_sections",
      calls: 2,
      succeeded: 1,
      failed: 1,
    });
    expect(buildSourceTelemetryContentGapRows(telemetry)[0]).toMatchObject({
      query: "missing guidance",
      tool: "search_sections",
      facets: ["Logical"],
    });
  });

  it("treats token-only telemetry as content", () => {
    const telemetry = sourceTelemetry({
      documents: [],
      toolUsage: [],
      missedQueries: [],
      tokenUsage: {
        inputTokens: 100,
        cachedInputTokens: null,
        outputTokens: 0,
        totalTokens: null,
      },
    });

    expect(buildSourceTelemetryAccordionItems(telemetry)).toEqual([]);
    expect(hasSourceTelemetryContent(telemetry)).toBe(true);
    expect(hasSourceTelemetryContent(null)).toBe(false);
  });
});

function sourceTelemetry(
  overrides: Partial<CodeReviewSourceTelemetryDto> = {},
): CodeReviewSourceTelemetryDto {
  return {
    schemaVersion: 1,
    summary: {
      documentCount: 1,
      snippetCount: 1,
      sourceHitCount: 3,
      toolCallCount: 2,
      missedQueryCount: 1,
    },
    documents: [
      {
        documentId: "doc_1",
        library: "zeeq-app",
        path: "/frontend/vue.md",
        title: "Vue guidance",
        hitCount: "2",
        usages: ["Searched", "Read"],
        readAfterSearch: true,
        facets: ["Logical"],
        bestRank: 1,
        bestScore: 0.9,
        queries: ["vue"],
        snippets: [
          {
            snippetId: "snippet_1",
            heading: "Vue guidance > Components",
            kind: "CodeSample",
            language: "vue",
            hitCount: 1,
            facets: ["Logical"],
            bestRank: "1",
            bestScore: "0.8",
            identifierMatch: true,
            queries: ["component"],
          },
        ],
      },
    ],
    toolUsage: [
      {
        tool: "search_sections",
        calls: "2",
        succeeded: 1,
        failed: 1,
      },
    ],
    missedQueries: [
      {
        query: "missing guidance",
        tool: "search_sections",
        facets: ["Logical"],
      },
    ],
    tokenUsage: null,
    ...overrides,
  };
}
