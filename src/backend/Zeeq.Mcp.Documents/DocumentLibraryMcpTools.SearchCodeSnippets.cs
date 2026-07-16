using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Documents.Snippets;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> SearchCodeSnippetsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_search_code_snippets_total",
            "The total number of times the search code snippets MCP tool is called."
        );

    /// <summary>
    /// Searches a library's fenced code blocks with combined semantic and full-text ranking.
    /// </summary>
    /// <remarks>
    /// Scoped to <see cref="SnippetKind.Code"/> — prose has its own <see cref="SearchSections"/>
    /// tool. See <see cref="SnippetSearchService"/> for the single-required-library and
    /// degraded-mode contract.
    /// </remarks>
    /// <param name="searchService">The injected snippet search orchestration service.</param>
    /// <param name="user">The authenticated caller; the active organization is read from its claims.</param>
    /// <param name="library">The library name to search within.</param>
    /// <param name="query">The task/topic intent, or symbols/APIs/errors to search for.</param>
    /// <param name="excludeDocumentPaths">Document paths already read; excluded from results.</param>
    /// <param name="maxResults">Maximum results to return; defaults to 5, clamped to 1..15.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Markdown results grouped by document, or a caller-facing error message.</returns>
    [McpServerTool(Name = "search_code_snippets", Title = "Search Code Snippets")]
    [Description(
        """
            Efficient retrieval of indexed, canonical code samples.

            Use this when planning/researching, about to act, or reviewing code where a
            canonical project-specific code pattern may exist.

            Search indexed docs for reference snippets, preferred API shapes, local
            idioms, boilerplate examples, tests, endpoint patterns, storage patterns,
            logging/telemetry patterns, UI state patterns, and migration examples.

            <search_kb_code_samples.triggers>
            - Planning/researching: identify implementation shape, files, patterns, or test strategy
            - About to act: implement, refactor, wire services, add tests, or fix failures
            - Reviewing code: compare against canonical snippets, API shape, or local idioms
            - Adding endpoints, tests, migrations, storage code, service wiring, logging, telemetry, integrations, or UI state
            - Knowing task intent but not the repo's exact API shape, local idiom, or layer pattern
            - When local file has conflicting patterns or inconsistencies and canonical conventions are needed
            </search_kb_code_samples.triggers>

            Query with implementation intent plus language, framework, layer, API names,
            known symbols, entities, activity (logging, exception handling, database, etc.), and adjacent terms.

            Prefer this over remembered approaches or general model knowledge for
            non-trivial code. Use search_sections first when you need design rationale
            or constraints. Skip it for tiny edits where no reusable pattern or
            implementation convention is involved.
            """
    )]
    public static async Task<string> SearchCodeSnippets(
        SnippetSearchService searchService,
        ClaimsPrincipal? user,
        [Description(
            "Required library name; if needed, use `list_libraries` to see available libraries."
        )]
            string library,
        [Description(
            "Describe the implementation task or pattern. Include file paths, symbols, APIs, frameworks, errors, feature names, and intent so semantic search can match the right examples. Example: \"C# ASP.NET Serilog logging and OpenTelemetry telemetry\""
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
            SnippetKind.Code,
            SearchCodeSnippetsCounter,
            cancellationToken
        );
}
