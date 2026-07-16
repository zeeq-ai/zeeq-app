using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Documents;

public sealed partial class DocumentLibraryMcpTools
{
    private static readonly Counter<int> SearchDocumentsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_search_documents_total",
            "The total number of times the search documents MCP tool is called."
        );

    /// <summary>
    /// Searches a manual document library with combined full-text and fuzzy title matching.
    /// </summary>
    /// <remarks>
    /// One query unions ranked full-text retrieval (every space-separated term is an independent
    /// OR alternative — see <c>PostgresLibraryDocumentStore.ToOrQueryText</c>) with trigram title
    /// matching, so a full-text hit always outranks a fuzzy-only hit and a document matching both
    /// ranks highest. Each hit reports its match type and per-signal scores so the caller can see
    /// why it surfaced.
    /// </remarks>
    /// <param name="store">The injected document-library store.</param>
    /// <param name="user">The authenticated caller; the active organization is read from its claims.</param>
    /// <param name="library">The library name to search within.</param>
    /// <param name="query">Space-separated keywords; every term is OR'd together. Also tolerates title typos.</param>
    /// <param name="limit">The maximum number of results to return; defaults to 10, capped at 50.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A JSON array of ranked search hits with match type and component scores.</returns>
    [McpServerTool(Name = "search_documents", Title = "Search Documents")]
    [Description(
        """
            Use to find the most relevant documents in a manual in the document library.
            These documents help write correct code; they are repository guidance and best practices.

            <search_documents.triggers>
            - Need to find guidance for key topics like logging, telemetry, error handling, coding practices, feature areas, technologies
            - The exact document path is not known and a topical search is needed to find the right content
            - Narrowing a large library to the documents relevant to a task before reading them
            </search_documents.triggers>

            Use `list_libraries` to find the library name and `read_document_by_path` to read a full result.
            Spaces OR terms together (broadens) — every keyword is an independent alternative, ranked by how many/how well terms match.
            Prefer this over `list_documents` when keywords and topics are already at hand and you need best practices or guidance.
            """
    )]
    public static async Task<string> SearchDocuments(
        ILibraryDocumentStore store,
        ClaimsPrincipal? user,
        [Description(
            "Required library name; use `list_libraries` to see available libraries; prefer known library if present"
        )]
            string library,
        [Description(
            """
                Space-separated keywords. Every term is OR'd together (broadens) — results ranked by
                how many/how well terms match, not filtered down to only rows matching every term.
                Include related words or synonyms to widen the net. Also tolerates title typos.
                No quote/exclude/or operator syntax — punctuation is treated as part of a literal
                search term, not a special operator.
                - example: logging telemetry structured-logging retry backoff
                """
        )]
            string query,
        [Description("Maximum results to return. Defaults to 10 and is capped at 50.")]
            int limit = 10,
        CancellationToken cancellationToken = default
    ) => await SearchAsync(store, user, library, query, limit, cancellationToken);
}
