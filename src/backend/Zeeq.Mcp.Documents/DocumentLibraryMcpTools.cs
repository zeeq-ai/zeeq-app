using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Core.Identity;
using ModelContextProtocol.Server;

namespace Zeeq.Mcp.Documents;

/// <summary>
/// MCP tools for manual document libraries.
/// </summary>
[McpServerToolType, Description("Provides Zeeq manual document-library MCP tools.")]
public sealed partial class DocumentLibraryMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    // Metrics-pipeline read counters (taxonomy names). Distinct from the OTEL-only
    // *_total call counters above: these carry organization_id so the capture pipeline
    // persists them for the "documents/sections/snippets read by library" dashboards.
    private static readonly Counter<int> DocumentReadCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_document_read_counter",
            "Documents read via read_document_by_path / list_documents, scoped by organization and library."
        );

    private static readonly Counter<int> SectionReadCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_section_read_counter",
            "Prose sections returned by search_sections, one increment per returned section."
        );

    private static readonly Counter<int> SnippetReadCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_snippet_read_counter",
            "Code snippets returned by search_code_snippets, one increment per returned snippet."
        );

    /// <summary>
    /// Runs the combined full-text and fuzzy search flow for the document search MCP tool.
    /// </summary>
    /// <remarks>
    /// Validation, library resolution, limit clamping, and result shaping live here so the public
    /// tool entrypoint stays small. Each result carries its match type and per-signal scores.
    /// </remarks>
    private static async Task<string> SearchAsync(
        ILibraryDocumentStore store,
        ClaimsPrincipal? user,
        string library,
        string query,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return RecordToolCall(
                SearchDocumentsCounter,
                user,
                "missing_query",
                [],
                "query is required."
            );
        }

        var resolved = await ResolveLibraryAsync(store, user, library, cancellationToken);
        if (resolved.Error is not null)
        {
            return RecordToolCall(
                SearchDocumentsCounter,
                user,
                "library_error",
                [("library", library)],
                resolved.Error
            );
        }

        var boundedLimit = Math.Clamp(limit, 1, 50);
        var matches = await store.SearchAsync(
            resolved.OrganizationId,
            resolved.Library!.Id,
            query,
            boundedLimit,
            cancellationToken
        );

        // Best-effort source attribution (no-op outside a code-review run). search_documents
        // surfaces whole documents, so each match is a Document-kind Searched hit. Relevance
        // ORDERING is preserved via Rank = the 1-based ordinal (the store returns matches in
        // server-side relevance order); this drives importance ranking (bestRank) in the review
        // snapshot, so no ordering signal is lost. Score stays 0 on purpose: document
        // full-text/fuzzy scores live on a different scale than the snippet RRF scores, so mixing
        // them into the aggregated bestScore (a max) would let document hits always dominate and
        // make bestScore meaningless. Ordinal rank, not raw score, is the doc-search signal.
        if (matches.Count == 0)
        {
            ToolTelemetrySink.RecordMissedQuery("search_documents", query);
        }
        else
        {
            for (var index = 0; index < matches.Count; index++)
            {
                var document = matches[index].Document;

                ToolTelemetrySink.RecordSource(
                    new(
                        ToolName: "search_documents",
                        Kind: ToolKnowledgeSourceKind.Document,
                        Usage: ToolKnowledgeSourceUsage.Searched,
                        Library: resolved.Library!.Name,
                        DocumentPath: document.Path,
                        DocumentTitle: document.Title,
                        Query: query,
                        DocumentId: document.Id,
                        Rank: index + 1
                    )
                );
            }
        }

        return RecordToolCall(
            SearchDocumentsCounter,
            user,
            "success",
            [("organization", resolved.OrganizationId), ("library", library)],
            ToJson(matches.Select(ToSearchResult))
        );
    }

    /// <summary>
    /// Resolves the caller's active organization from claims, then resolves the library name to its
    /// stable id.
    /// </summary>
    /// <remarks>
    /// The organization is read from the server-issued claims on the authenticated principal, never
    /// from caller input, so an agent cannot reach another tenant's documents. Document MCP tools
    /// accept a human-readable library name because that is what agents can reason about; store
    /// operations use the immutable library id, so every document tool goes through this helper
    /// before listing, reading, or searching documents.
    /// </remarks>
    private static async Task<ResolvedLibrary> ResolveLibraryAsync(
        ILibraryDocumentStore store,
        ClaimsPrincipal? user,
        string library,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user?.AsZeeqMinimalIdentity().OrganizationId;
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return new(string.Empty, null, "Active organization is required.");
        }

        if (string.IsNullOrWhiteSpace(library))
        {
            return new(organizationId, null, "library is required.");
        }

        var resolved = await store.GetLibraryAsync(organizationId, library, cancellationToken);

        return resolved is null
            ? new(
                organizationId,
                null,
                $"Library '{library}' was not found; use the list_libraries tool to get valid libraries."
            )
            : new(organizationId, resolved, null);
    }

    /// <summary>
    /// Records a document-library MCP tool invocation: annotates the current activity and increments
    /// the tool's counter with the caller, outcome, and scope tags, then returns the response.
    /// </summary>
    /// <remarks>
    /// Centralizing the tag shape keeps every document tool reporting the same dimensions. Returning
    /// the response lets each tool meter and return in a single statement. The caller identity is the
    /// stable subject claim, never request input.
    /// </remarks>
    /// <param name="counter">The per-tool invocation counter.</param>
    /// <param name="user">The authenticated caller, used for the <c>user</c> dimension.</param>
    /// <param name="result">The outcome label (for example <c>success</c> or <c>not_found</c>).</param>
    /// <param name="scope">Additional scope tags such as organization and library.</param>
    /// <param name="response">The tool response to return unchanged.</param>
    /// <returns>The supplied <paramref name="response"/>.</returns>
    private static string RecordToolCall(
        Counter<int> counter,
        ClaimsPrincipal? user,
        string result,
        (string Key, object? Value)[] scope,
        string response
    )
    {
        (string Key, object? Value)[] tags =
        [
            ("result", result),
            ("user", user?.AuthenticatedUser()?.Sub ?? "unknown"),
            .. scope,
        ];

        ZeeqTelemetry.SetTags(tags);
        counter.Increment(tags: tags);

        return response;
    }

    /// <summary>
    /// Emits a metrics-pipeline knowledge-read counter (document/section/snippet) with the
    /// promoted tags the capture rule persists. Absent <c>organization_id</c> means the pipeline
    /// skips persistence; the measurement still flows to OTEL.
    /// </summary>
    private static void RecordKnowledgeRead(
        Counter<int> counter,
        ClaimsPrincipal? user,
        string organizationId,
        string toolName,
        string library,
        string? path,
        string? heading = null
    )
    {
        List<(string Key, object? Value)> tags = [("tool_name", toolName), ("library", library)];

        if (!string.IsNullOrEmpty(organizationId))
        {
            tags.Add(("organization_id", (object)organizationId));
        }

        var email = user?.AuthenticatedUser()?.Email;
        if (!string.IsNullOrEmpty(email))
        {
            tags.Add(("user", (object)email));
        }

        if (!string.IsNullOrEmpty(path))
        {
            tags.Add(("path", (object)path));
        }

        // Only sections/snippets carry a heading (bounded by ingested content, not by traffic —
        // same cardinality class as `path`), so it's opt-in: full-document reads pass nothing.
        if (!string.IsNullOrEmpty(heading))
        {
            tags.Add(("heading", (object)heading));
        }

        counter.Increment(tags: [.. tags]);
    }

    /// <summary>
    /// Runs the hybrid snippet search flow shared by <c>search_sections</c> and
    /// <c>search_code_snippets</c> — only <paramref name="kind"/> and the per-tool
    /// <paramref name="counter"/> differ between the two.
    /// </summary>
    /// <remarks>
    /// The org is resolved from claims and handed to <see cref="SnippetSearchService"/>
    /// unvalidated (empty string if missing) — the service itself validates org/library/query and
    /// returns a single caller-facing error either way, so this helper does not duplicate that
    /// validation; it only needs to distinguish "validation/not-found error" from "success" for
    /// telemetry.
    /// </remarks>
    private static async Task<string> SearchSnippetsAsync(
        SnippetSearchService searchService,
        ClaimsPrincipal? user,
        string library,
        string query,
        string[]? excludeDocumentPaths,
        int maxResults,
        SnippetKind kind,
        Counter<int> counter,
        CancellationToken cancellationToken
    )
    {
        var organizationId = user?.AsZeeqMinimalIdentity().OrganizationId ?? string.Empty;

        var outcome = await searchService.SearchAsync(
            new SnippetSearchRequest(
                organizationId,
                library,
                kind,
                query,
                excludeDocumentPaths,
                maxResults
            ),
            cancellationToken
        );

        if (outcome.Error is not null)
        {
            return RecordToolCall(
                counter,
                user,
                "library_error",
                [("library", library)],
                outcome.Error
            );
        }

        var result = outcome.Result!;

        // Best-effort source attribution (no-op outside a code-review run). Each surfaced row
        // carries BOTH the snippet (heading/kind/language) and its owning document (path/title),
        // so document importance can aggregate from snippet hits in the review snapshot. See
        // Zeeq.Platform.CodeReviews.CodeReviewSourceTelemetry.
        var toolName = kind == SnippetKind.Code ? "search_code_snippets" : "search_sections";
        var sourceKind =
            kind == SnippetKind.Code
                ? ToolKnowledgeSourceKind.CodeSample
                : ToolKnowledgeSourceKind.Section;
        var readCounter = kind == SnippetKind.Code ? SnippetReadCounter : SectionReadCounter;

        if (result.Rows.Count == 0)
        {
            // Zero-result search → content-gap signal, attributed to the facet by the collector.
            ToolTelemetrySink.RecordMissedQuery(toolName, query);
        }
        else
        {
            foreach (var row in result.Rows)
            {
                ToolTelemetrySink.RecordSource(
                    new(
                        ToolName: toolName,
                        Kind: sourceKind,
                        Usage: ToolKnowledgeSourceUsage.Searched,
                        Library: result.LibraryName,
                        DocumentPath: row.DocumentPath,
                        DocumentTitle: row.DocumentTitle,
                        Heading: row.HeadingPath,
                        Language: row.Language,
                        Query: query,
                        DocumentId: row.DocumentId,
                        SnippetId: row.SnippetId,
                        Rank: BestArmRank(row.VectorRank, row.TextRank),
                        Score: row.Score,
                        IdentifierMatch: row.IdentifierMatch
                    )
                );

                // One metrics-pipeline read per returned section/snippet (powers the UI-7
                // path leaderboard and the section-level leaderboard via the heading tag).
                RecordKnowledgeRead(
                    readCounter,
                    user,
                    organizationId,
                    toolName,
                    library,
                    row.DocumentPath,
                    row.HeadingPath
                );
            }
        }

        return RecordToolCall(
            counter,
            user,
            result.Degraded ? "success_degraded" : "success",
            [("organization", organizationId), ("library", library)],
            FormatSnippetResults(query, result)
        );
    }

    /// <summary>
    /// Formats ranked snippet rows as markdown, grouped by owning document, in the same rank
    /// order the store returned (best-scoring row's document appears first).
    /// </summary>
    /// <remarks>
    /// <c>GroupBy</c> is stable in LINQ-to-Objects: both group order and within-group row order
    /// match the input order, so no explicit re-sort is needed after grouping.
    /// <para/>
    /// NOTE: grouping by <c>(DocumentPath, DocumentTitle)</c> assumes one document always reports
    /// the same path/title across all its rows (flagged by code review, 2026-07-11) — this holds
    /// because both come from the same joined document row (<c>d.path</c>, <c>d.title</c> in
    /// <c>SearchAsync</c>'s SQL) for every snippet belonging to that document; they are non-null
    /// columns, never per-snippet values.
    /// </remarks>
    private static string FormatSnippetResults(string query, SnippetSearchResult result)
    {
        if (result.Rows.Count == 0)
        {
            return $"No results for \"{query}\" in library '{result.LibraryName}'. "
                + "Try broader terms, fewer excluded paths, or a different library.";
        }

        var markdown = new StringBuilder();
        markdown.AppendLine($"# Results for: \"{query}\"");
        markdown.AppendLine(
            "(Use read_document_by_path with the source path to read the full document)"
        );
        markdown.AppendLine();

        foreach (var group in result.Rows.GroupBy(row => (row.DocumentPath, row.DocumentTitle)))
        {
            markdown.AppendLine(
                $"## {group.Key.DocumentTitle} — `zeeq://{group.Key.DocumentPath.TrimStart('/')}` (library: {result.LibraryName})"
            );
            markdown.AppendLine();

            foreach (var row in group)
            {
                markdown.AppendLine($"### {row.HeadingPath}");

                if (row.Language is not null)
                {
                    var fence = CodeFence(row.Content);
                    markdown.AppendLine($"{fence}{row.Language}");
                    markdown.AppendLine(row.Content);
                    markdown.AppendLine(fence);
                }
                else
                {
                    markdown.AppendLine(row.Content);
                }

                markdown.AppendLine();
            }
        }

        if (result.Degraded)
        {
            markdown.AppendLine(
                "_Semantic ranking was unavailable for this search; results are full-text only._"
            );
        }

        return markdown.ToString().TrimEnd();
    }

    /// <summary>
    /// Picks a backtick fence at least one character longer than the longest run of backticks in
    /// <paramref name="content"/>, so a snippet that itself contains a fenced code block (e.g. a
    /// documentation example showing markdown syntax) cannot prematurely close the outer fence.
    /// </summary>
    /// <remarks>
    /// Flagged by code review (2026-07-11): a snippet body containing <c>```</c> would otherwise
    /// terminate the wrapping fence early and garble the rest of the tool response. Standard
    /// markdown-safety technique — mirrors how GitHub/CommonMark renderers themselves pick a
    /// longer fence to nest code blocks.
    /// </remarks>
    private static string CodeFence(string content)
    {
        var longestRun = 0;
        var currentRun = 0;

        foreach (var character in content)
        {
            currentRun = character == '`' ? currentRun + 1 : 0;
            longestRun = Math.Max(longestRun, currentRun);
        }

        return new string('`', Math.Max(3, longestRun + 1));
    }

    /// <summary>
    /// Projects a stored document to the terse list-document summary (table-of-contents index).
    /// </summary>
    private static DocumentSummary ToSummary(LibraryDocument document) =>
        new(document.Id, document.Path, document.Title, document.Keywords, document.Headings);

    /// <summary>
    /// Projects a combined-search hit to its JSON shape, adding match type and per-signal scores.
    /// </summary>
    /// <remarks>
    /// The shape is flat — document fields sit alongside the match metadata rather than nested under
    /// a wrapper — so it stays consistent with the list-document summary and is simpler for a
    /// consuming agent to read.
    /// </remarks>
    private static SearchResultSummary ToSearchResult(LibraryDocumentMatch match) =>
        new(
            Id: match.Document.Id,
            Path: match.Document.Path,
            Title: match.Document.Title,
            Keywords: match.Document.Keywords,
            Headings: match.Document.Headings,
            TokenCount: match.Document.TokenCount,
            ProcessingStatus: match.Document.ProcessingStatus.ToString(),
            UpdatedAt: match.Document.UpdatedAt,
            MatchType: match.MatchType.ToString(),
            FullTextScore: match.FullTextScore,
            FuzzyScore: match.FuzzyScore
        );

    /// <summary>
    /// Returns the better (smaller) of two 1-based arm ranks, ignoring 0 (0 = not a hit in that
    /// arm); returns 0 only when the row ranked in neither arm.
    /// </summary>
    /// <remarks>
    /// A snippet row can hit the vector arm, the full-text arm, or both. The best rank across the
    /// arms is the strongest position signal for importance ordering in the review snapshot
    /// (<c>br</c> / bestRank in <c>CodeReviewSourceTelemetry</c>).
    /// </remarks>
    /// <param name="vectorRank">1-based HNSW vector-arm rank; 0 when not a vector-arm hit.</param>
    /// <param name="textRank">1-based full-text-arm rank; 0 when not a full-text-arm hit.</param>
    /// <returns>The smaller non-zero rank, or 0 when both are 0.</returns>
    private static int BestArmRank(int vectorRank, int textRank)
    {
        if (vectorRank <= 0)
        {
            return textRank <= 0 ? 0 : textRank;
        }

        if (textRank <= 0)
        {
            return vectorRank;
        }

        return Math.Min(vectorRank, textRank);
    }

    /// <summary>
    /// Serializes MCP tool responses with stable web-style JSON property names.
    /// </summary>
    private static string ToJson<T>(IEnumerable<T> value) =>
        JsonSerializer.Serialize(value.ToArray(), JsonOptions);

    /// <summary>
    /// Result of resolving the caller's active organization and a library name.
    /// </summary>
    /// <param name="OrganizationId">The active organization read from the caller's claims.</param>
    /// <param name="Library">The resolved library when lookup succeeds.</param>
    /// <param name="Error">A caller-facing validation or not-found message when lookup fails.</param>
    private sealed record ResolvedLibrary(string OrganizationId, Library? Library, string? Error);

    /// <summary>
    /// Compact document shape returned by MCP list tools (table-of-contents index).
    /// </summary>
    /// <param name="Id">Stable document identifier.</param>
    /// <param name="Path">Normalized document path.</param>
    /// <param name="Title">Display title resolved from Markdown.</param>
    /// <param name="Keywords">Normalized front-matter keywords.</param>
    /// <param name="Headings">Plain heading text in document order.</param>
    private sealed record DocumentSummary(
        string Id,
        string Path,
        string Title,
        string[] Keywords,
        string[] Headings
    );

    /// <summary>
    /// A combined-search hit: the document fields plus why it matched and how strongly.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="DocumentSummary"/> and adds match metadata at the same level, keeping the
    /// search payload flat for the consuming agent.
    /// </remarks>
    /// <param name="Id">Stable document identifier.</param>
    /// <param name="Path">Normalized document path.</param>
    /// <param name="Title">Display title resolved from Markdown.</param>
    /// <param name="Keywords">Normalized front-matter keywords.</param>
    /// <param name="Headings">Plain heading text in document order.</param>
    /// <param name="TokenCount">Estimated token count for searchable content.</param>
    /// <param name="ProcessingStatus">Secondary indexing state.</param>
    /// <param name="UpdatedAt">Timestamp when the document was last updated.</param>
    /// <param name="MatchType">Which retrieval signal(s) matched: FullText, Fuzzy, or Both.</param>
    /// <param name="FullTextScore">Normalized full-text rank in [0, 1); 0 when not a full-text hit.</param>
    /// <param name="FuzzyScore">Trigram title similarity in [0, 1]; 0 when not a fuzzy hit.</param>
    private sealed record SearchResultSummary(
        string Id,
        string Path,
        string Title,
        string[] Keywords,
        string[] Headings,
        int TokenCount,
        string ProcessingStatus,
        DateTimeOffset UpdatedAt,
        string MatchType,
        double FullTextScore,
        double FuzzyScore
    );
}
