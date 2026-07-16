using System.Text.Json.Serialization;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Aggregated, compact source-telemetry snapshot for one code review.
/// </summary>
/// <remarks>
/// Produced by <c>CodeReviewTelemetryContext.Snapshot()</c> and serialized into the review
/// record's <c>SourceTelemetryPayload</c> jsonb column. It answers "which knowledge-base
/// documents/snippets did the reviewers consult, and how" so importance aggregates from
/// snippet hits up to owning documents.
///
/// Compact wire keys (<c>[JsonPropertyName]</c>) keep the repeated key strings small in the
/// nested-array stored payload while C# names stay descriptive. This record doubles as the
/// GitHub-comment render input; the public API exposes a separate readable DTO
/// (<c>CodeReviewSourceTelemetryDto</c>) so a storage <see cref="SchemaVersion" /> bump is not
/// an API break. The stored keys are also the input contract for the offline analytics ETL.
/// </remarks>
/// <example>
/// The stored compact jsonb shape (short keys keep the repeated nested-array keys small):
/// <code>
/// {
///   "v": 1,
///   "sum": { "docs": 2, "snips": 2, "hits": 7, "calls": 6, "miss": 1 },
///   "docs": [
///     {
///       "id": "doc_01H", "lib": "zeeq-app",
///       "p": "/backend/dotnet-csharp-best-practices.md",
///       "t": "C# 14 (CSharp), .NET 10, EF General Guidelines",
///       "hc": 5, "u": ["Searched", "Read"], "ras": true, "f": ["Security", "Performance"],
///       "br": 1, "bs": 0.0312,
///       "q": ["structured logging with serilog"],
///       "sn": [
///         { "sid": "sn_01H", "h": "Logging and OpenTelemetry (OTEL) Tracing", "k": "Section", "lang": null,
///           "hc": 3, "f": ["Security", "Performance"], "br": 1, "bs": 0.0312, "im": true,
///           "q": ["otel tracing spans", "structured logging"] },
///         { "sid": "sn_02J", "h": "Database Storage with Postgres and Npgsql", "k": "CodeSample", "lang": "csharp",
///           "hc": 1, "f": ["Performance"], "br": 4, "bs": 0.0121, "im": false,
///           "q": ["npgsql batching pattern"] }
///       ]
///     },
///     { "id": "doc_01J", "lib": "zeeq-app", "p": "/backend/web-api-endpoints-openapi.md",
///       "t": "Web API Endpoints and OpenAPI",
///       "hc": 2, "u": ["Read"], "ras": false, "f": ["Structural"], "br": 0, "bs": 0, "q": [], "sn": [] }
///   ],
///   "tools": [
///     { "n": "search_code_snippets", "c": 2, "ok": 2, "err": 0 },
///     { "n": "search_sections", "c": 2, "ok": 2, "err": 0 },
///     { "n": "search_documents", "c": 1, "ok": 1, "err": 0 },
///     { "n": "read_document_by_path", "c": 1, "ok": 1, "err": 0 }
///   ],
///   "miss": [
///     { "q": "aspire distributed lock pattern", "tool": "search_sections", "f": ["Structural"] }
///   ]
/// }
/// </code>
/// Key legend — top level: <c>v</c>=schemaVersion, <c>sum</c>=summary, <c>docs</c>=documents,
/// <c>tools</c>=toolUsage, <c>miss</c>=missedQueries. Document/snippet: <c>id</c>=documentId,
/// <c>lib</c>=library, <c>p</c>=path, <c>t</c>=title, <c>hc</c>=hitCount, <c>u</c>=usages
/// (Searched/Read), <c>ras</c>=readAfterSearch (searched then later read), <c>f</c>=facets,
/// <c>q</c>=queries, <c>br</c>=bestRank (min 1-based rank across arms; 0=none),
/// <c>bs</c>=bestScore (max fused score), <c>sn</c>=snippets, <c>sid</c>=snippetId,
/// <c>h</c>=heading, <c>k</c>=kind (Section/CodeSample), <c>lang</c>=language,
/// <c>im</c>=identifierMatch. Tool usage: <c>n</c>/<c>c</c>/<c>ok</c>/<c>err</c>=tool
/// name/calls/succeeded/failed.
/// </example>
/// <param name="SchemaVersion">Storage schema version; bump when the compact shape changes.</param>
/// <param name="Summary">Roll-up counts across the snapshot.</param>
/// <param name="Documents">Consulted documents, ordered by importance (hit count then rank).</param>
/// <param name="ToolUsage">Per-tool call/success/failure counts, including list tools.</param>
/// <param name="MissedQueries">Searches that returned zero rows (the content-gap signal).</param>
public sealed record CodeReviewSourceTelemetry(
    [property: JsonPropertyName("v")] int SchemaVersion,
    [property: JsonPropertyName("sum")] CodeReviewSourceSummary Summary,
    [property: JsonPropertyName("docs")] IReadOnlyList<CodeReviewSourceDocument> Documents,
    [property: JsonPropertyName("tools")] IReadOnlyList<CodeReviewToolUsage> ToolUsage,
    [property: JsonPropertyName("miss")] IReadOnlyList<CodeReviewMissedQuery> MissedQueries
)
{
    /// <summary>The current storage schema version emitted by new snapshots.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>An empty snapshot (no sources surfaced); serialized when a run consulted nothing.</summary>
    public static CodeReviewSourceTelemetry Empty { get; } =
        new(CurrentSchemaVersion, new(0, 0, 0, 0, 0), [], [], []);

    /// <summary>Gets whether the snapshot carries nothing worth storing or rendering.</summary>
    [JsonIgnore]
    public bool IsEmpty => Documents.Count == 0 && ToolUsage.Count == 0 && MissedQueries.Count == 0;
}

/// <summary>Roll-up counts for a <see cref="CodeReviewSourceTelemetry" /> snapshot.</summary>
/// <param name="DocumentCount">Distinct documents consulted.</param>
/// <param name="SnippetCount">Distinct snippets surfaced across all documents.</param>
/// <param name="SourceHitCount">Total raw source hits (document + snippet level).</param>
/// <param name="ToolCallCount">Total tool calls observed, including list tools.</param>
/// <param name="MissedQueryCount">Distinct zero-result searches.</param>
public sealed record CodeReviewSourceSummary(
    [property: JsonPropertyName("docs")] int DocumentCount,
    [property: JsonPropertyName("snips")] int SnippetCount,
    [property: JsonPropertyName("hits")] int SourceHitCount,
    [property: JsonPropertyName("calls")] int ToolCallCount,
    [property: JsonPropertyName("miss")] int MissedQueryCount
);

/// <summary>One consulted document and the snippets surfaced within it.</summary>
/// <param name="DocumentId">Stable document id (for ETL joins); not rendered.</param>
/// <param name="Library">Library the document belongs to.</param>
/// <param name="Path">Document path at review time.</param>
/// <param name="Title">Document title at review time.</param>
/// <param name="HitCount">Document-level plus all snippet hits for this document.</param>
/// <param name="Usages">Distinct usages observed (<c>Searched</c>, <c>Read</c>).</param>
/// <param name="ReadAfterSearch">Whether the doc was both searched and later read (relevance proxy).</param>
/// <param name="Facets">Distinct reviewer facets that surfaced this document.</param>
/// <param name="BestRank">Min 1-based rank across search arms; 0 when never a ranked hit.</param>
/// <param name="BestScore">Max fused relevance score across hits.</param>
/// <param name="Queries">Distinct whole-document queries that surfaced this document.</param>
/// <param name="Snippets">Snippets surfaced within this document, ordered by importance.</param>
public sealed record CodeReviewSourceDocument(
    [property: JsonPropertyName("id")] string DocumentId,
    [property: JsonPropertyName("lib")] string Library,
    [property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("t")] string Title,
    [property: JsonPropertyName("hc")] int HitCount,
    [property: JsonPropertyName("u")] IReadOnlyList<string> Usages,
    [property: JsonPropertyName("ras")] bool ReadAfterSearch,
    [property: JsonPropertyName("f")] IReadOnlyList<string> Facets,
    [property: JsonPropertyName("br")] int BestRank,
    [property: JsonPropertyName("bs")] double BestScore,
    [property: JsonPropertyName("q")] IReadOnlyList<string> Queries,
    [property: JsonPropertyName("sn")] IReadOnlyList<CodeReviewSourceSnippet> Snippets
);

/// <summary>One snippet (prose section or code sample) surfaced within a document.</summary>
/// <param name="SnippetId">Stable snippet id (for ETL joins); not rendered.</param>
/// <param name="Heading">Snippet heading path.</param>
/// <param name="Kind">Snippet kind: <c>Section</c> or <c>CodeSample</c>.</param>
/// <param name="Language">Fence language for code samples; null for sections.</param>
/// <param name="HitCount">Number of raw hits that surfaced this snippet.</param>
/// <param name="Facets">Distinct reviewer facets that surfaced this snippet.</param>
/// <param name="BestRank">Min 1-based rank across search arms; 0 when never a ranked hit.</param>
/// <param name="BestScore">Max fused relevance score across hits.</param>
/// <param name="IdentifierMatch">Whether any contributing hit had an identifier overlap.</param>
/// <param name="Queries">Distinct queries that surfaced this snippet.</param>
public sealed record CodeReviewSourceSnippet(
    [property: JsonPropertyName("sid")] string SnippetId,
    [property: JsonPropertyName("h")] string Heading,
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("lang")] string? Language,
    [property: JsonPropertyName("hc")] int HitCount,
    [property: JsonPropertyName("f")] IReadOnlyList<string> Facets,
    [property: JsonPropertyName("br")] int BestRank,
    [property: JsonPropertyName("bs")] double BestScore,
    [property: JsonPropertyName("im")] bool IdentifierMatch,
    [property: JsonPropertyName("q")] IReadOnlyList<string> Queries
);

/// <summary>Per-tool invocation counts observed during a review run.</summary>
/// <param name="Tool">MCP tool name, e.g. <c>search_code_snippets</c>.</param>
/// <param name="Calls">Total invocations.</param>
/// <param name="Succeeded">Invocations that completed successfully.</param>
/// <param name="Failed">Invocations that threw.</param>
public sealed record CodeReviewToolUsage(
    [property: JsonPropertyName("n")] string Tool,
    [property: JsonPropertyName("c")] int Calls,
    [property: JsonPropertyName("ok")] int Succeeded,
    [property: JsonPropertyName("err")] int Failed
);

/// <summary>A search that returned zero rows — the content-gap signal.</summary>
/// <param name="Query">The query that returned nothing.</param>
/// <param name="Tool">The tool that produced no rows.</param>
/// <param name="Facets">Distinct reviewer facets that issued this missed query.</param>
public sealed record CodeReviewMissedQuery(
    [property: JsonPropertyName("q")] string Query,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("f")] IReadOnlyList<string> Facets
);
