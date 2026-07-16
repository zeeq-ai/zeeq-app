using System.Collections.Concurrent;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Run-scoped collector for code-review knowledge-source and tool telemetry.
/// </summary>
/// <remarks>
/// One instance is created per review run by the runner and passed to
/// <see cref="ICodeReviewAgentExecutor.ExecuteAsync" />. Each reviewer agent is decorated with
/// <see cref="CodeReviewTelemetryMiddleware" />, which — on every tool call — reads this context
/// back from <c>AIAgent.CurrentRunContext.RunOptions.AdditionalProperties</c>, opens the ambient
/// <see cref="ToolTelemetrySink" /> scope plus a facet scope, and records the invocation outcome.
/// The document tools then report their sources through the sink into this collector.
///
/// Concurrency: reviewers run concurrently in the fan-out workflow, and one reviewer can fan its
/// own tool calls out too, so raw hits land in <see cref="ConcurrentBag{T}" />s. The empirical
/// proof that the run context reaches the middleware under
/// <c>InProcessExecution.Concurrent</c> — with correct per-facet attribution and no loss — is the
/// Phase 0 spike recorded in the feature plan. <see cref="Snapshot" /> collapses the raw hits into
/// the compact, document-centric <see cref="CodeReviewSourceTelemetry" /> for storage and render.
/// </remarks>
public sealed class CodeReviewTelemetryContext(
    string? organizationId = null,
    string? repositoryId = null
) : IToolTelemetrySink
{
    /// <summary>Organization that owns this review; the metrics scope for emitted review metrics.</summary>
    public string? OrganizationId { get; } = organizationId;

    /// <summary>Repository under review, when resolved; null for agent reviews with no mapped repo.</summary>
    public string? RepositoryId { get; } = repositoryId;

    /// <summary>Caps that hard-bound the stored payload even for a chatty review (draft §2.3).</summary>
    private const int MaxQueriesPerNode = 10;
    private const int MaxFacetsPerNode = 10;
    private const int MaxSnippetsPerDocument = 25;
    private const int MaxDocuments = 50;
    private const int MaxMissedQueries = 25;

    private readonly ConcurrentBag<RawSource> _sources = [];
    private readonly ConcurrentBag<RawToolCall> _toolCalls = [];
    private readonly ConcurrentBag<RawMiss> _misses = [];
    private readonly AsyncLocal<FacetMarker?> _currentFacet = new();

    /// <summary>
    /// Sets the reviewer facet attributed to sources and misses recorded inside the returned scope.
    /// </summary>
    /// <remarks>
    /// Opened per tool call by <see cref="CodeReviewTelemetryMiddleware" /> and disposed (LIFO, via
    /// <c>using</c>) when the call returns, so a source recorded by the document tool inherits the
    /// facet of the reviewer that invoked it.
    ///
    /// NOTE: the scope installs a unique <see cref="FacetMarker" /> reference and only unwinds when
    /// that exact marker is still current (see <see cref="FacetScope" />). Today tools are leaf
    /// operations, but the Agent Framework supports additional tools and agents-as-tools, which
    /// could make tool invocation re-entrant on a single async flow; the identity guard prevents an
    /// inner scope with a different facet from being clobbered by an outer scope's disposal — a
    /// future footgun a bare single-slot restore would hide.
    /// </remarks>
    /// <param name="facet">The reviewer facet, e.g. <c>Security</c>.</param>
    /// <returns>A disposable that restores the previously active facet.</returns>
    public IDisposable BeginToolInvocationScope(string facet)
    {
        var previous = _currentFacet.Value;
        var marker = new FacetMarker(facet);
        _currentFacet.Value = marker;

        return new FacetScope(this, marker, previous);
    }

    /// <inheritdoc />
    public void RecordSource(ToolKnowledgeSource source) =>
        _sources.Add(new(source, _currentFacet.Value?.Facet));

    /// <inheritdoc />
    public void RecordMissedQuery(string toolName, string query) =>
        _misses.Add(new(toolName, query, _currentFacet.Value?.Facet));

    /// <summary>Records one tool invocation outcome observed by the middleware.</summary>
    /// <param name="tool">The invoked tool name, e.g. <c>search_sections</c>.</param>
    /// <param name="succeeded">Whether the invocation completed without throwing.</param>
    public void RecordToolInvocation(string tool, bool succeeded) =>
        _toolCalls.Add(new(tool, succeeded));

    /// <summary>
    /// Aggregates the raw concurrent hits into the compact, document-centric snapshot for storage.
    /// </summary>
    /// <remarks>
    /// Documents group by <c>(library, path)</c> and snippets by <c>(heading, kind, language)</c>;
    /// counts, distinct facets/queries, and relevance signals (bestRank/bestScore/identifierMatch)
    /// are folded in and then order-then-capped so the least-important tail drops first. Returns
    /// <see cref="CodeReviewSourceTelemetry.Empty" /> when nothing was consulted.
    /// </remarks>
    /// <returns>The aggregated snapshot; <see cref="CodeReviewSourceTelemetry.Empty" /> when empty.</returns>
    public CodeReviewSourceTelemetry Snapshot()
    {
        var toolCalls = _toolCalls.ToArray();
        var misses = _misses.ToArray();

        var documents = BuildDocuments(_sources.ToArray());
        var toolUsage = BuildToolUsage(toolCalls);
        var missedQueries = BuildMissedQueries(misses);

        if (documents.Count == 0 && toolUsage.Count == 0 && missedQueries.Count == 0)
        {
            return CodeReviewSourceTelemetry.Empty;
        }

        var summary = new CodeReviewSourceSummary(
            DocumentCount: documents.Count,
            SnippetCount: documents.Sum(document => document.Snippets.Count),
            SourceHitCount: documents.Sum(document => document.HitCount),
            ToolCallCount: toolCalls.Length,
            MissedQueryCount: missedQueries.Count
        );

        return new CodeReviewSourceTelemetry(
            CodeReviewSourceTelemetry.CurrentSchemaVersion,
            summary,
            documents,
            toolUsage,
            missedQueries
        );
    }

    /// <summary>
    /// Snapshots the collected telemetry and serializes it to the stored jsonb payload, mapping an
    /// empty snapshot to the <see cref="CodeReviewRecord.EmptySourceTelemetryPayload"/> sentinel.
    /// </summary>
    /// <remarks>
    /// The single place the runners turn a run's collected sources into the value persisted on
    /// <see cref="CodeReviewRecord.SourceTelemetryPayload"/>, so success and errored (partial)
    /// paths in both runners agree on the empty-vs-populated mapping.
    /// </remarks>
    /// <returns>The compact jsonb payload, or <c>"{}"</c> when nothing was consulted.</returns>
    public string SerializeSnapshotPayload()
    {
        var snapshot = Snapshot();

        return snapshot.IsEmpty
            ? CodeReviewRecord.EmptySourceTelemetryPayload
            : CodeReviewSourceTelemetrySerializer.Serialize(snapshot);
    }

    /// <summary>
    /// Groups source hits into documents (by library+path) and their snippets, ordered by
    /// importance (hit count, then best rank, then title) and capped.
    /// </summary>
    private static IReadOnlyList<CodeReviewSourceDocument> BuildDocuments(
        IReadOnlyList<RawSource> sources
    )
    {
        // Only Document/Section/CodeSample populate docs; Index listings never appear here.
        var relevant = sources
            .Where(raw => raw.Source.Kind != ToolKnowledgeSourceKind.Index)
            .ToArray();

        if (relevant.Length == 0)
        {
            return [];
        }

        return
        [
            .. relevant
                .GroupBy(raw =>
                    (raw.Source.Library ?? string.Empty, raw.Source.DocumentPath ?? string.Empty)
                )
                .Select(group => BuildDocument(group.Key.Item1, group.Key.Item2, [.. group]))
                .OrderByDescending(document => document.HitCount)
                .ThenBy(document => RankSortKey(document.BestRank))
                .ThenBy(document => document.Title, StringComparer.Ordinal)
                .Take(MaxDocuments),
        ];
    }

    private static CodeReviewSourceDocument BuildDocument(
        string library,
        string path,
        IReadOnlyList<RawSource> rows
    )
    {
        var usages = DistinctOrdered(rows.Select(row => row.Source.Usage.ToString()));

        // Snippets are rows that carry a heading; document-level hits (search_documents / reads)
        // have a null heading and contribute only to the document totals below.
        var snippets = rows.Where(row => !string.IsNullOrEmpty(row.Source.Heading))
            .GroupBy(row => (row.Source.Heading!, row.Source.Kind, row.Source.Language))
            .Select(group =>
                BuildSnippet(group.Key.Item1, group.Key.Item2, group.Key.Item3, [.. group])
            )
            .OrderByDescending(snippet => snippet.HitCount)
            .ThenBy(snippet => RankSortKey(snippet.BestRank))
            .Take(MaxSnippetsPerDocument)
            .ToArray();

        return new CodeReviewSourceDocument(
            DocumentId: FirstId(rows.Select(row => row.Source.DocumentId)),
            Library: library,
            Path: path,
            Title: FirstId(rows.Select(row => row.Source.DocumentTitle)),
            HitCount: rows.Count,
            Usages: usages,
            // Both searched and later read → the agent chose to open it (strong relevance proxy).
            ReadAfterSearch: usages.Contains(nameof(ToolKnowledgeSourceUsage.Searched))
                && usages.Contains(nameof(ToolKnowledgeSourceUsage.Read)),
            Facets: Cap(DistinctFacets(rows.Select(row => row.Facet)), MaxFacetsPerNode),
            BestRank: MinNonZeroRank(rows.Select(row => row.Source.Rank)),
            BestScore: rows.Max(row => row.Source.Score),
            // Whole-document queries only; snippet-search queries belong to their snippet.
            Queries: Cap(
                DistinctQueries(rows.Where(row => string.IsNullOrEmpty(row.Source.Heading))),
                MaxQueriesPerNode
            ),
            Snippets: snippets
        );
    }

    private static CodeReviewSourceSnippet BuildSnippet(
        string heading,
        ToolKnowledgeSourceKind kind,
        string? language,
        IReadOnlyList<RawSource> rows
    ) =>
        new(
            SnippetId: FirstId(rows.Select(row => row.Source.SnippetId)),
            Heading: heading,
            Kind: kind.ToString(),
            Language: language,
            HitCount: rows.Count,
            Facets: Cap(DistinctFacets(rows.Select(row => row.Facet)), MaxFacetsPerNode),
            BestRank: MinNonZeroRank(rows.Select(row => row.Source.Rank)),
            BestScore: rows.Max(row => row.Source.Score),
            IdentifierMatch: rows.Any(row => row.Source.IdentifierMatch),
            Queries: Cap(DistinctQueries(rows), MaxQueriesPerNode)
        );

    private static IReadOnlyList<CodeReviewToolUsage> BuildToolUsage(
        IReadOnlyList<RawToolCall> calls
    ) =>
        [
            .. calls
                .GroupBy(call => call.Tool)
                .Select(group => new CodeReviewToolUsage(
                    Tool: group.Key,
                    Calls: group.Count(),
                    Succeeded: group.Count(call => call.Succeeded),
                    Failed: group.Count(call => !call.Succeeded)
                ))
                .OrderByDescending(usage => usage.Calls)
                .ThenBy(usage => usage.Tool, StringComparer.Ordinal),
        ];

    private static IReadOnlyList<CodeReviewMissedQuery> BuildMissedQueries(
        IReadOnlyList<RawMiss> misses
    ) =>
        [
            // Order-then-cap by importance: a (tool, query) missed more often (initial + correction
            // retries, or across facets) is the more actionable content gap, so it survives the cap
            // first; tool/query are the deterministic tie-break.
            .. misses
                .GroupBy(miss => (miss.Tool, miss.Query))
                .Select(group =>
                    (
                        Occurrences: group.Count(),
                        Miss: new CodeReviewMissedQuery(
                            Query: group.Key.Query,
                            Tool: group.Key.Tool,
                            Facets: Cap(
                                DistinctFacets(group.Select(miss => miss.Facet)),
                                MaxFacetsPerNode
                            )
                        )
                    )
                )
                .OrderByDescending(entry => entry.Occurrences)
                .ThenBy(entry => entry.Miss.Tool, StringComparer.Ordinal)
                .ThenBy(entry => entry.Miss.Query, StringComparer.Ordinal)
                .Take(MaxMissedQueries)
                .Select(entry => entry.Miss),
        ];

    /// <summary>First non-empty value in the sequence (stable id/title), or empty string.</summary>
    private static string FirstId(IEnumerable<string?> values) =>
        values.FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;

    /// <summary>Distinct non-blank facet labels, trimmed and ordinal-ordered for a stable payload.</summary>
    private static IReadOnlyList<string> DistinctFacets(IEnumerable<string?> facets) =>
        [
            .. facets
                .Where(facet => !string.IsNullOrWhiteSpace(facet))
                .Select(facet => facet!.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(facet => facet, StringComparer.Ordinal),
        ];

    /// <summary>Distinct non-blank queries, trimmed and ordinal-ordered for a stable payload.</summary>
    private static IReadOnlyList<string> DistinctQueries(IEnumerable<RawSource> rows) =>
        [
            .. rows.Select(row => row.Source.Query)
                .Where(query => !string.IsNullOrWhiteSpace(query))
                .Select(query => query!.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(query => query, StringComparer.Ordinal),
        ];

    /// <summary>Distinct usages ("Searched"/"Read"), ordinal-ordered.</summary>
    private static IReadOnlyList<string> DistinctOrdered(IEnumerable<string> values) =>
        [
            .. values
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal),
        ];

    /// <summary>Smallest non-zero rank across the hits; 0 when none was ever a ranked hit.</summary>
    private static int MinNonZeroRank(IEnumerable<int> ranks)
    {
        var ranked = ranks.Where(rank => rank > 0).ToArray();

        return ranked.Length == 0 ? 0 : ranked.Min();
    }

    /// <summary>Maps a best-rank to an ascending sort key so 0 (no rank) sorts last, not first.</summary>
    private static int RankSortKey(int bestRank) => bestRank == 0 ? int.MaxValue : bestRank;

    private static IReadOnlyList<string> Cap(IReadOnlyList<string> values, int max) =>
        values.Count <= max ? values : [.. values.Take(max)];

    /// <summary>One raw source hit plus the reviewer facet active when it was recorded.</summary>
    private sealed record RawSource(ToolKnowledgeSource Source, string? Facet);

    /// <summary>One raw tool invocation outcome.</summary>
    private sealed record RawToolCall(string Tool, bool Succeeded);

    /// <summary>One raw zero-result search plus the reviewer facet that issued it.</summary>
    private sealed record RawMiss(string Tool, string Query, string? Facet);

    /// <summary>
    /// Unique per-scope marker holding the active facet.
    /// </summary>
    /// <remarks>
    /// A distinct reference per <see cref="BeginToolInvocationScope" /> call lets
    /// <see cref="FacetScope" /> guard restoration by object identity, which stays correct even when
    /// two overlapping scopes carry the same facet string.
    /// </remarks>
    private sealed record FacetMarker(string Facet);

    /// <summary>Restores the previous facet when a tool-invocation scope is disposed.</summary>
    /// <remarks>
    /// Only unwinds when the marker this scope installed is still current; if a newer nested scope
    /// (e.g. an agent-as-tool) is active, it is left in place rather than clobbered. Under the
    /// normal LIFO <c>using</c> disposal this is equivalent to a plain restore.
    /// </remarks>
    private sealed class FacetScope(
        CodeReviewTelemetryContext owner,
        FacetMarker installed,
        FacetMarker? previous
    ) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (ReferenceEquals(owner._currentFacet.Value, installed))
            {
                owner._currentFacet.Value = previous;
            }
        }
    }
}
