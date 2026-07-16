using System.Diagnostics;
using System.Diagnostics.Metrics;
using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pgvector;

namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// Orchestrates hybrid (vector + full-text) snippet search across the private and public
/// document tables.
/// </summary>
/// <remarks>
/// A caller (an agent, via an MCP tool, or the HTTP search endpoint) always names exactly one
/// library — there is no "search all libraries" mode. The caller already knows which library is
/// relevant to its current task; a broad default would run every search against every library the
/// org has and return results the caller has no way to scope back down. A resolved
/// <see cref="Library"/> is either privately or publicly sourced, never both
/// (<see cref="Library.PublicSourceId"/>), so dispatch to the matching
/// <see cref="ISnippetStore{TDocument}"/> is a single call, not a fan-out or merge step.
/// <para/>
/// The query embedding is asymmetric versus indexed content: an instruction prefix is applied
/// only here, never at index time (see <see cref="SnippetComposer"/>'s plain payload text), and
/// only against the <see cref="EmbeddingClientProfile.Query"/>-profile generator (near-zero SDK
/// retries, so a slow provider degrades to full-text search rather than blocking the caller). The
/// embedding is cached in <see cref="HybridCache"/> so repeated queries for the same text don't
/// re-hit the provider.
/// </remarks>
public sealed partial class SnippetSearchService(
    ILibraryDocumentStore libraryStore,
    ISnippetStore<LibraryDocument> privateSnippetStore,
    ISnippetStore<DocsPublicDocument> publicSnippetStore,
    [FromKeyedServices(DefaultLlmChatClientKeys.SnippetEmbeddingsQuery)]
        IEmbeddingGenerator<string, Embedding<float>> queryEmbeddingGenerator,
    LlmEmbeddingSettings embeddingSettings,
    HybridCache cache,
    ILogger<SnippetSearchService> log
)
{
    private const int DefaultMaxResults = 5;
    private const int MaxResultsCap = 15;

    private static readonly Counter<int> SearchCounter = ZeeqTelemetry.Metrics.CreateCounter<int>(
        "zeeq_snippet_search_total",
        "The total number of snippet searches, tagged by kind/result/degraded."
    );

    private static readonly TimeSpan QueryEmbeddingTimeout = TimeSpan.FromSeconds(2);

    private static readonly HybridCacheEntryOptions QueryEmbeddingCacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Resolves the request's library, embeds the query (degrading to full-text-only on failure
    /// or timeout), and runs one fused search against the applicable store.
    /// </summary>
    /// <param name="request">The caller's search request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SnippetSearchOutcome"/> carrying either the ranked result or a caller-facing
    /// validation/not-found error.
    /// </returns>
    public async Task<SnippetSearchOutcome> SearchAsync(
        SnippetSearchRequest request,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.OrganizationId))
        {
            return new(null, "Active organization is required.", SnippetSearchErrorKind.Validation);
        }

        if (string.IsNullOrWhiteSpace(request.LibraryName))
        {
            return new(null, "library is required.", SnippetSearchErrorKind.Validation);
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new(null, "query is required.", SnippetSearchErrorKind.Validation);
        }

        var library = await libraryStore.GetLibraryAsync(
            request.OrganizationId,
            request.LibraryName,
            ct
        );

        if (library is null)
        {
            return new(
                null,
                $"Library '{request.LibraryName}' was not found; use the list_libraries tool to get valid libraries.",
                SnippetSearchErrorKind.NotFound
            );
        }

        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "documents.snippets.search",
            ActivityKind.Internal
        );
        activity?.SetTag("organization.id", request.OrganizationId);
        activity?.SetTag("library.id", library.Id);
        activity?.SetTag("kind", request.Kind.ToString());

        var maxResults = Math.Clamp(request.MaxResults ?? DefaultMaxResults, 1, MaxResultsCap);
        var excludedPaths = NormalizeExcludedPaths(request.ExcludedDocumentPaths);
        var queryIdentifiers = SnippetIdentifierExtractor.Extract(
            request.Query,
            SnippetIdentifierExtractor.QueryMinLength
        );
        var (queryEmbedding, degraded) = await TryEmbedQueryAsync(request.Kind, request.Query, ct);

        var rows = library.PublicSourceId is { } publicSourceId
            ? await publicSnippetStore.SearchAsync(
                new SnippetSearchQuery(
                    OrganizationId: null,
                    LibraryId: null,
                    PublicSourceId: publicSourceId,
                    Kind: request.Kind,
                    QueryText: request.Query,
                    QueryEmbedding: queryEmbedding,
                    QueryIdentifiers: queryIdentifiers,
                    ExcludedDocumentPaths: excludedPaths,
                    Limit: maxResults
                ),
                ct
            )
            : await privateSnippetStore.SearchAsync(
                new SnippetSearchQuery(
                    OrganizationId: request.OrganizationId,
                    LibraryId: library.Id,
                    PublicSourceId: null,
                    Kind: request.Kind,
                    QueryText: request.Query,
                    QueryEmbedding: queryEmbedding,
                    QueryIdentifiers: queryIdentifiers,
                    ExcludedDocumentPaths: excludedPaths,
                    Limit: maxResults
                ),
                ct
            );

        activity?.SetTag("degraded", degraded);
        activity?.SetTag("results.count", rows.Count);

        SearchCounter.Add(
            1,
            new KeyValuePair<string, object?>("kind", request.Kind.ToString()),
            new KeyValuePair<string, object?>("result", "succeeded"),
            new KeyValuePair<string, object?>("degraded", degraded)
        );

        if (degraded)
        {
            LogDegradedSearch(request.LibraryName, request.Kind.ToString());
        }

        return new(new SnippetSearchResult(library.Name, degraded, rows), null);
    }

    /// <summary>
    /// Generates and caches the instruction-prefixed query embedding, degrading to full-text-only
    /// mode on any provider failure or timeout.
    /// </summary>
    /// <remarks>
    /// The 2-second timeout is enforced with a linked token so a slow provider call never blocks
    /// the caller past that bound — a real cancellation of <paramref name="ct"/> (the caller went
    /// away) still propagates rather than being swallowed into a degraded result. The float array
    /// is cached (not <see cref="HalfVector"/> directly) since that is the shape
    /// <see cref="IEmbeddingGenerator{String,Embedding}"/> returns and it round-trips cleanly
    /// through <see cref="HybridCache"/>'s serializer; the <see cref="HalfVector"/> conversion for
    /// storage/query happens once per call, on the (possibly cached) result.
    /// </remarks>
    private async Task<(HalfVector? Embedding, bool Degraded)> TryEmbedQueryAsync(
        SnippetKind kind,
        string query,
        CancellationToken ct
    )
    {
        var cacheKey =
            $"snippet-query-embedding:{embeddingSettings.Model}:{embeddingSettings.Dimensions}:{kind}:{query}";

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(QueryEmbeddingTimeout);

            var vector = await cache.GetOrCreateAsync(
                cacheKey,
                (Generator: queryEmbeddingGenerator, Query: query, embeddingSettings.Dimensions),
                static async (state, token) =>
                {
                    var payload =
                        $"Instruct: Retrieve documentation and code snippets relevant to the query\nQuery: {state.Query}";

                    var result = await state.Generator.GenerateAsync(
                        [payload],
                        new EmbeddingGenerationOptions { Dimensions = state.Dimensions },
                        token
                    );

                    return result[0].Vector.ToArray();
                },
                QueryEmbeddingCacheOptions,
                cancellationToken: timeoutSource.Token
            );

            return (ToHalfVector(vector), false);
        }
        catch (Exception ex)
        {
            // NOTE: this intentionally degrades on ANY embedding failure (timeout, provider
            // fault, malformed response — not just cancellation), per the spec's "on miss/failure
            // → QueryEmbedding = null" contract (secondary-indexing spec, Phase 5). Narrowing this
            // to `catch (OperationCanceledException)` only (flagged by four independent code
            // reviewers, code review follow-up 2026-07-11) would make a real provider fault (e.g.
            // an HTTP 500, not a cancellation) propagate unhandled instead of degrading — a
            // regression against the spec, and it would break
            // SearchAsync_WhenEmbeddingGeneratorThrows_DegradesToFullTextOnly, which asserts
            // exactly this. `ct.IsCancellationRequested` is a synchronous flag read (not racy)
            // checked only after the awaited call has already thrown, so it reliably distinguishes
            // "the caller's own token was already cancelled before we got here" (rethrow — the
            // caller went away, nothing to degrade for) from every other failure (degrade). See
            // SearchAsync_WhenCallerCancels_PropagatesCancellationInsteadOfDegrading for the
            // caller-cancellation side of this contract.
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            LogQueryEmbeddingFailed(ex);

            return (null, true);
        }
    }

    /// <summary>
    /// Normalizes caller-supplied exclusion paths to the same canonical form stored document
    /// paths use, so <c>NOT (d.path = ANY(...))</c> comparisons in the store's SQL actually match.
    /// </summary>
    /// <remarks>
    /// Blank entries are dropped silently; a malformed path (one <see cref="DocumentNormalizer"/>
    /// rejects) is dropped rather than failing the whole search — an exclusion the caller
    /// misspelled should not prevent the search from running.
    /// </remarks>
    private static string[] NormalizeExcludedPaths(string[]? excludedDocumentPaths)
    {
        if (excludedDocumentPaths is null || excludedDocumentPaths.Length == 0)
        {
            return [];
        }

        var normalized = new List<string>(excludedDocumentPaths.Length);

        foreach (var path in excludedDocumentPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                normalized.Add(DocumentNormalizer.NormalizePath(path));
            }
            catch (ArgumentException)
            {
                // Malformed exclusion path — skip it rather than fail the whole search.
            }
        }

        return normalized.ToArray();
    }

    /// <summary>
    /// Converts a raw <c>float[]</c> embedding to the <see cref="HalfVector"/> shape the snippet
    /// stores' <c>halfvec(768)</c> columns and <c>&lt;=&gt;</c> distance operator expect.
    /// </summary>
    private static HalfVector ToHalfVector(float[] vector)
    {
        var halves = new Half[vector.Length];

        for (var i = 0; i < vector.Length; i++)
        {
            halves[i] = (Half)vector[i];
        }

        return new HalfVector(halves);
    }

    [LoggerMessage(
        EventId = 2900,
        Level = LogLevel.Warning,
        Message = "Query-embedding generation failed or timed out; degrading to full-text-only snippet search."
    )]
    private partial void LogQueryEmbeddingFailed(Exception exception);

    [LoggerMessage(
        EventId = 2901,
        Level = LogLevel.Information,
        Message = "Snippet search for library '{LibraryName}' (kind: {Kind}) is running in degraded (full-text-only) mode."
    )]
    private partial void LogDegradedSearch(string libraryName, string kind);
}
