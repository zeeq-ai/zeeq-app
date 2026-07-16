using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Documents.Snippets;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> SearchSectionsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_search_sections_total",
            "The total number of times the search sections MCP tool is called."
        );

    /// <summary>
    /// Searches a library's prose document sections with combined semantic and full-text ranking.
    /// </summary>
    /// <remarks>
    /// Scoped to <see cref="SnippetKind.Section"/> — fenced code is excluded, since code has its
    /// own <see cref="SearchCodeSnippets"/> tool. See <see cref="SnippetSearchService"/> for the
    /// single-required-library and degraded-mode contract.
    /// </remarks>
    /// <param name="searchService">The injected snippet search orchestration service.</param>
    /// <param name="user">The authenticated caller; the active organization is read from its claims.</param>
    /// <param name="library">The library name to search within.</param>
    /// <param name="query">The task/topic intent, or symbols/APIs/errors to search for.</param>
    /// <param name="excludeDocumentPaths">Document paths already read; excluded from results.</param>
    /// <param name="maxResults">Maximum results to return; defaults to 5, clamped to 1..15.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Markdown results grouped by document, or a caller-facing error message.</returns>
    [McpServerTool(Name = "search_sections", Title = "Search Document Sections")]
    [Description(
        """
            Efficient retrieval of indexed sections of canonical guidance.

            Use this when planning/researching, about to act, or reviewing code where
            repo-specific prose guidance may affect the answer.

            Search canonical knowledge-base sections for conceptual guidance,
            constraints, rationale, tradeoffs, edge cases, and best practices.

            <search_kb_sections.triggers>
            - Planning/researching: choose an approach, draft a plan, understand context
            - About to act: implement, refactor, debug, explain, or recommend
            - Reviewing code: check canonical rules, conventions, rationale, or edge cases
            - Code samples are too narrow; contextual explanation is needed
            </search_kb_sections.triggers>

            Query with task intent and relevant key phrases for semantic matches

            Prefer this over reading whole documents when you need focused guidance. Use
            search_code_snippets instead when you need concrete code patterns.
            """
    )]
    public static async Task<string> SearchSections(
        SnippetSearchService searchService,
        ClaimsPrincipal? user,
        [Description(
            "Required library name; if needed, use `list_libraries` to see available libraries."
        )]
            string library,
        [Description(
            "Describe the current task or topic. Include file paths, symbols, APIs, frameworks, errors, feature names, and intent so semantic search can match the right guidance. Example: \"Python asyncio best practices for high throughput concurrent programming\""
        )]
            string query,
        [Description("Optional document paths already read; excluded from results.")]
            string[]? excludeDocumentPaths = null,
        [Description("Optional maximum results to return. Defaults to 5, clamped to 1..15.")]
            int maxResults = 5,
        CancellationToken cancellationToken = default
    ) =>
        await SearchSnippetsAsync(
            searchService,
            user,
            library,
            query,
            excludeDocumentPaths,
            maxResults,
            SnippetKind.Section,
            SearchSectionsCounter,
            cancellationToken
        );
}
