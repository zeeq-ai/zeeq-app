using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Parsing;
using Zeeq.Core.Documents.Snippets;
using Zeeq.Core.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Open.ChannelExtensions;
using Pgvector;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Periodically claims documents pending secondary indexing, parses and composes their snippets,
/// and reconciles them into the snippet tables — the write path for hybrid snippet search.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sweep, not messages.</b> Document write paths already set
/// <see cref="DocumentProcessingStatus.Pending"/> on content change; this service is the single
/// mechanism that drains that backlog — handling steady-state, first-run backfill, and crash
/// recovery (stale <see cref="DocumentProcessingStatus.Indexing"/> rows) with one claim query.
/// </para>
/// <para>
/// <b>Drain, not one-batch-per-tick.</b> Each tick repeatedly claims rounds of
/// <see cref="SnippetIndexingSettings.ClaimBatchSize"/> documents until a round comes back empty,
/// so a large backfill runs at full pipeline throughput rather than trickling out one batch every
/// <see cref="SnippetIndexingSettings.SweepIntervalSeconds"/>.
/// </para>
/// <para>
/// <b>I/O fan-out, single-writer DB.</b> Parsing/composition (Pipeline A) and embedding
/// (Pipeline B) are both CPU/I/O-bound stages that fan out across
/// <see cref="SnippetIndexingSettings.MaxParseConcurrency"/> /
/// <see cref="SnippetIndexingSettings.MaxEmbeddingConcurrency"/> respectively; each pipeline's DB
/// writer runs single-threaded so its scoped <c>DbContext</c> is never touched concurrently and
/// write ordering is deterministic.
/// </para>
/// <para>
/// <b>Embedding is SDK-native resilience, not this class's job.</b> Pipeline B calls the
/// <c>Batch</c>-profile keyed <see cref="IEmbeddingGenerator{String,Embedding}"/> (see
/// <c>LlmClientFactory.CreateDefaultEmbeddingGenerator</c>); retries/backoff/<c>Retry-After</c>
/// happen entirely inside that call via <c>System.ClientModel.ClientRetryPolicy</c>. A batch that
/// still throws after the SDK's own retries are exhausted has its lease released for the next
/// tick — this class only decides "succeeded" vs. "release and retry later," never how many times
/// to retry.
/// </para>
/// <para>
/// <b>Registration.</b> Like <c>IngestSchedulerHostedService</c>, this is a live
/// <see cref="BackgroundService"/> registered worker-only (plus Development, so the single-process
/// local Aspire topology runs it). The atomic <c>FOR UPDATE SKIP LOCKED</c> claim (both the
/// document claim and the embedding-lease claim) makes concurrent replicas safe.
/// </para>
/// </remarks>
public sealed partial class SnippetIndexingHostedService(
    IServiceScopeFactory scopeFactory,
    SnippetIndexingSettings settings,
    LlmEmbeddingSettings embeddingSettings,
    ILogger<SnippetIndexingHostedService> logger
) : BackgroundService
{
    private static readonly Counter<int> IndexDocumentsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_snippet_index_documents_total",
            "The total number of documents processed by Pipeline A (parse/compose/reconcile), tagged by table/result."
        );

    private static readonly Counter<int> EmbeddingsCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_snippet_embeddings_total",
            "The total number of snippet embedding outcomes (succeeded/released)."
        );

    private static readonly Histogram<double> EmbeddingDurationHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<double>(
            "zeeq_snippet_embedding_duration_ms",
            "The elapsed time for one embedding provider call, including any SDK-internal retries."
        );

    private static readonly UpDownCounter<int> EmbeddingInflightCounter =
        ZeeqTelemetry.Metrics.CreateUpDownCounter<int>(
            "zeeq_snippet_embedding_inflight",
            "The number of embedding provider calls currently in flight."
        );

    private static readonly Counter<int> SweepDrainedCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(
            "zeeq_snippet_sweep_drained_total",
            "The total number of documents fully processed (Pipeline A) per table."
        );

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Enabled)
        {
            LogSweepDisabled();
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.SweepIntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepTickFailed(ex);
            }
        }
    }

    /// <summary>
    /// Runs one sweep pass across both document tables: Pipeline A (parse/compose/reconcile) then
    /// Pipeline B (embed), for both the private and public tables. Internal so tests can drive it
    /// directly without waiting on the <see cref="PeriodicTimer"/>.
    /// </summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        using var activity = ZeeqTelemetry.Tracer.StartActivity(
            "documents.snippets.sweep",
            ActivityKind.Internal
        );

        var privateCount = await RunPrivatePipelineAsync(activity, cancellationToken);
        var publicCount = await RunPublicPipelineAsync(activity, cancellationToken);

        activity?.SetTag("documents.claimed", privateCount + publicCount);

        SweepDrainedCounter.Add(
            privateCount,
            new KeyValuePair<string, object?>("table", "private")
        );
        SweepDrainedCounter.Add(publicCount, new KeyValuePair<string, object?>("table", "public"));

        var privateEmbedded = await RunPrivateEmbeddingPipelineAsync(activity, cancellationToken);
        var publicEmbedded = await RunPublicEmbeddingPipelineAsync(activity, cancellationToken);

        activity?.SetTag("documents.embedded", privateEmbedded + publicEmbedded);
    }

    /// <summary>
    /// Pipeline A for private library documents: claim rounds → parse/compose (fan-out) →
    /// single-threaded reconcile + status stamp. Returns the number of documents processed.
    /// </summary>
    private Task<int> RunPrivatePipelineAsync(
        Activity? activity,
        CancellationToken cancellationToken
    ) =>
        RunPipelineAsync(
            tag: "private",
            resolveDocStore: sp => sp.GetRequiredService<ILibraryDocumentStore>(),
            resolveSnippetStore: sp => sp.GetRequiredService<ISnippetStore<LibraryDocument>>(),
            compose: document => TryCompose(document.Id, document.Content, document.Title),
            activity,
            cancellationToken
        );

    /// <summary>
    /// Pipeline A for global public documents. Mirrors the private pipeline against the public
    /// snippet table.
    /// </summary>
    private Task<int> RunPublicPipelineAsync(
        Activity? activity,
        CancellationToken cancellationToken
    ) =>
        RunPipelineAsync(
            tag: "public",
            resolveDocStore: sp => sp.GetRequiredService<IDocsPublicDocumentStore>(),
            resolveSnippetStore: sp => sp.GetRequiredService<ISnippetStore<DocsPublicDocument>>(),
            compose: document => TryCompose(document.Id, document.Content, document.Title),
            activity,
            cancellationToken
        );

    /// <summary>
    /// Pipeline A, generic over the document type: claim rounds (each round on its own scope, via
    /// <see cref="ClaimRoundsAsync{TDocument}"/>) → parse/compose (fan-out) → single-threaded
    /// reconcile + status stamp. Returns the number of documents processed.
    /// </summary>
    /// <remarks>
    /// <b>One write scope per pipeline run, not per document.</b> The write stage below is forced
    /// single-threaded (<c>maxConcurrency: 1</c>), so its scoped <c>DbContext</c> is never touched
    /// concurrently regardless of how many documents pass through it — resolving the write-side
    /// stores once here (instead of once per document write) removes per-item scope-creation/service-
    /// resolution overhead without changing that guarantee. This scope is still fully isolated from
    /// <see cref="ClaimRoundsAsync{TDocument}"/>'s own per-round scopes: claiming and writing use
    /// separate <c>DbContext</c> instances throughout, exactly as before — only the write side's
    /// per-document re-resolution was collapsed.
    /// </remarks>
    private async Task<int> RunPipelineAsync<TDocument>(
        string tag,
        Func<IServiceProvider, IIndexableDocumentStore<TDocument>> resolveDocStore,
        Func<IServiceProvider, ISnippetStore<TDocument>> resolveSnippetStore,
        Func<TDocument, IReadOnlyList<ComposedSnippet>?> compose,
        Activity? activity,
        CancellationToken cancellationToken
    )
    {
        var processed = 0;
        // Plain locals, not Interlocked — the writer stage below runs at maxConcurrency: 1, so
        // this closure is never invoked concurrently with itself.
        var succeededCount = 0;
        var failedCount = 0;

        await using var writeScope = scopeFactory.CreateAsyncScope();
        var docStore = resolveDocStore(writeScope.ServiceProvider);
        var snippetStore = resolveSnippetStore(writeScope.ServiceProvider);

        await ClaimRoundsAsync(resolveDocStore, cancellationToken)
            .ToChannel(
                new BoundedChannelOptions(settings.PipelineCapacity) { SingleReader = true },
                deferredExecution: false,
                cancellationToken: cancellationToken
            )
            .PipeAsync(
                maxConcurrency: settings.MaxParseConcurrency,
                capacity: settings.PipelineCapacity,
                transform: document =>
                    ValueTask.FromResult((Document: document, Snippets: compose(document))),
                cancellationToken: cancellationToken
            )
            .ReadAllConcurrentlyAsync(
                maxConcurrency: 1,
                async item =>
                {
                    Interlocked.Increment(ref processed);
                    var succeeded = await WriteAsync(
                        docStore,
                        snippetStore,
                        item.Document,
                        item.Snippets,
                        cancellationToken
                    );

                    if (succeeded)
                    {
                        succeededCount++;
                    }
                    else
                    {
                        failedCount++;
                    }
                },
                cancellationToken
            );

        activity?.SetTag($"documents.claimed.{tag}", processed);

        // Aggregated once per pipeline run (not per document) — mirrors SweepDrainedCounter's
        // once-per-run emission below, code review follow-up 2026-07-11.
        if (succeededCount > 0)
        {
            IndexDocumentsCounter.Add(
                succeededCount,
                new KeyValuePair<string, object?>("table", tag),
                new KeyValuePair<string, object?>("result", "succeeded")
            );
        }

        if (failedCount > 0)
        {
            IndexDocumentsCounter.Add(
                failedCount,
                new KeyValuePair<string, object?>("table", tag),
                new KeyValuePair<string, object?>("result", "failed")
            );
        }

        return processed;
    }

    /// <summary>
    /// Pipeline B for private library document snippets: lease-claim rounds → embed (fan-out) →
    /// single-threaded write-back. No-ops when <see cref="LlmEmbeddingSettings.Enabled"/> is
    /// false — snippets stay full-text-searchable with a null embedding.
    /// </summary>
    private Task<int> RunPrivateEmbeddingPipelineAsync(
        Activity? activity,
        CancellationToken cancellationToken
    ) =>
        RunEmbeddingPipelineAsync(
            tag: "private",
            resolveSnippetStore: sp => sp.GetRequiredService<ISnippetStore<LibraryDocument>>(),
            activity,
            cancellationToken
        );

    /// <summary>
    /// Pipeline B for global public document snippets. Mirrors the private embedding pipeline.
    /// </summary>
    private Task<int> RunPublicEmbeddingPipelineAsync(
        Activity? activity,
        CancellationToken cancellationToken
    ) =>
        RunEmbeddingPipelineAsync(
            tag: "public",
            resolveSnippetStore: sp => sp.GetRequiredService<ISnippetStore<DocsPublicDocument>>(),
            activity,
            cancellationToken
        );

    /// <summary>
    /// Pipeline B, generic over the document type: lease-claim rounds of
    /// <see cref="SnippetIndexingSettings.EmbeddingBatchSize"/> snippets → embed at
    /// <see cref="SnippetIndexingSettings.MaxEmbeddingConcurrency"/> in-flight provider calls
    /// (the I/O fan-out — this is the pipeline stage that actually waits on network I/O) →
    /// single-threaded write-back (vectors on success, lease release on failure). Returns the
    /// number of snippets embedded.
    /// </summary>
    /// <remarks>
    /// One write scope and one resolved generator instance for the whole pipeline run, same
    /// reasoning as <see cref="RunPipelineAsync{TDocument}"/>'s write scope: the writer stage is
    /// forced single-threaded (<c>maxConcurrency: 1</c>), so reusing one scope across every write
    /// in this run is safe and avoids per-batch scope-creation overhead.
    /// </remarks>
    private async Task<int> RunEmbeddingPipelineAsync<TDocument>(
        string tag,
        Func<IServiceProvider, ISnippetStore<TDocument>> resolveSnippetStore,
        Activity? activity,
        CancellationToken cancellationToken
    )
    {
        if (!embeddingSettings.Enabled)
        {
            return 0;
        }

        var processed = 0;
        var currentModel = $"{embeddingSettings.Model}@{embeddingSettings.Dimensions}";
        var lease = TimeSpan.FromMinutes(settings.EmbeddingLeaseMinutes);

        await using var writeScope = scopeFactory.CreateAsyncScope();
        var snippetStore = resolveSnippetStore(writeScope.ServiceProvider);
        var generator = writeScope.ServiceProvider.GetRequiredKeyedService<
            IEmbeddingGenerator<string, Embedding<float>>
        >(DefaultLlmChatClientKeys.SnippetEmbeddingsBatch);

        await ClaimEmbeddingRoundsAsync(resolveSnippetStore, currentModel, lease, cancellationToken)
            .ToChannel(
                new BoundedChannelOptions(settings.MaxEmbeddingConcurrency) { SingleReader = true },
                deferredExecution: false,
                cancellationToken: cancellationToken
            )
            .PipeAsync(
                maxConcurrency: settings.MaxEmbeddingConcurrency,
                capacity: settings.MaxEmbeddingConcurrency,
                transform: async batch =>
                {
                    using var embedActivity = ZeeqTelemetry.Tracer.StartActivity(
                        "documents.snippets.embed_batch",
                        ActivityKind.Client,
                        parentContext: default,
                        tags:
                        [
                            new KeyValuePair<string, object?>("batch.size", batch.Count),
                            new KeyValuePair<string, object?>("embedding.model", currentModel),
                        ]
                    );

                    EmbeddingInflightCounter.Add(1);
                    var stopwatch = Stopwatch.StartNew();

                    try
                    {
                        // Retry lives entirely inside this call (the Batch-profile SDK client's
                        // ClientRetryPolicy) — no retry logic belongs here. A batch that still
                        // throws has already exhausted the SDK's own retries.
                        var vectors = await generator.GenerateAsync(
                            batch.Select(claim => claim.EmbeddingPayload),
                            new EmbeddingGenerationOptions
                            {
                                Dimensions = embeddingSettings.Dimensions,
                            },
                            cancellationToken
                        );

                        return (
                            Batch: batch,
                            Vectors: (GeneratedEmbeddings<Embedding<float>>?)vectors
                        );
                    }
                    catch (Exception ex)
                    {
                        embedActivity?.AddException(ex);
                        LogEmbeddingBatchFailed(ex, batch.Count);

                        return (Batch: batch, Vectors: null);
                    }
                    finally
                    {
                        EmbeddingInflightCounter.Add(-1);
                        EmbeddingDurationHistogram.Record(stopwatch.Elapsed.TotalMilliseconds);
                    }
                },
                cancellationToken: cancellationToken
            )
            .ReadAllConcurrentlyAsync(
                maxConcurrency: 1,
                async item =>
                {
                    if (item.Vectors is null)
                    {
                        await snippetStore.ReleaseEmbeddingClaimsAsync(
                            item.Batch.Select(claim => claim.Id).ToArray(),
                            cancellationToken
                        );

                        EmbeddingsCounter.Add(
                            item.Batch.Count,
                            new KeyValuePair<string, object?>("result", "released")
                        );

                        return;
                    }

                    var results = item
                        .Batch.Zip(
                            item.Vectors,
                            (claim, embedding) =>
                                new EmbeddingResult(claim.Id, ToHalfVector(embedding))
                        )
                        .ToArray();

                    await snippetStore.SetEmbeddingsAsync(results, currentModel, cancellationToken);

                    Interlocked.Add(ref processed, results.Length);
                    EmbeddingsCounter.Add(
                        results.Length,
                        new KeyValuePair<string, object?>("result", "succeeded")
                    );
                },
                cancellationToken
            );

        activity?.SetTag($"documents.embedded.{tag}", processed);

        return processed;
    }

    /// <summary>
    /// Claims embedding-lease rounds until a round returns empty, so one tick drains the
    /// embedding backlog. Each round uses its own DI scope, isolated from the writer's single
    /// scope for this pipeline run (see <see cref="RunEmbeddingPipelineAsync{TDocument}"/>).
    /// </summary>
    private async IAsyncEnumerable<
        IReadOnlyList<EmbeddingClaim>
    > ClaimEmbeddingRoundsAsync<TDocument>(
        Func<IServiceProvider, ISnippetStore<TDocument>> resolveSnippetStore,
        string embeddingModel,
        TimeSpan lease,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = resolveSnippetStore(scope.ServiceProvider);

            var claimed = await store.ClaimMissingEmbeddingsAsync(
                embeddingModel,
                lease,
                settings.EmbeddingBatchSize,
                cancellationToken
            );

            if (claimed.Count == 0)
            {
                yield break;
            }

            yield return claimed;
        }
    }

    /// <summary>
    /// Converts a generated MEAI embedding to the <c>halfvec(768)</c>-compatible Pgvector type —
    /// see <see cref="LibraryDocumentSnippet.Embedding"/> for why <see cref="HalfVector"/>, not
    /// <see cref="Vector"/>, is required here.
    /// </summary>
    private static HalfVector ToHalfVector(Embedding<float> embedding)
    {
        var span = embedding.Vector.Span;
        var halves = new Half[span.Length];

        for (var i = 0; i < span.Length; i++)
        {
            halves[i] = (Half)span[i];
        }

        return new HalfVector(halves);
    }

    /// <summary>
    /// Claims documents in rounds until a round returns empty, so one tick drains the backlog.
    /// Each round uses its own DI scope so the claim's <c>DbContext</c> is isolated from the
    /// writer's single scope for this pipeline run (see <see cref="RunPipelineAsync{TDocument}"/>).
    /// </summary>
    private async IAsyncEnumerable<TDocument> ClaimRoundsAsync<TDocument>(
        Func<IServiceProvider, IIndexableDocumentStore<TDocument>> resolveStore,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var staleAfter = TimeSpan.FromMinutes(settings.StaleIndexingMinutes);

        while (!cancellationToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = resolveStore(scope.ServiceProvider);

            var claimed = await store.ClaimPendingIndexingAsync(
                settings.ClaimBatchSize,
                staleAfter,
                cancellationToken
            );

            if (claimed.Count == 0)
                yield break;

            foreach (var document in claimed)
                yield return document;
        }
    }

    /// <summary>
    /// Parses and composes snippets for one document. Returns null on parse/compose failure — the
    /// writer maps that to a <see cref="DocumentProcessingStatus.Failed"/> stamp so one bad
    /// document never stalls the pass. Pure CPU; no DB access, safe to run on the fan-out stage.
    /// </summary>
    private IReadOnlyList<ComposedSnippet>? TryCompose(
        string documentId,
        string content,
        string title
    )
    {
        try
        {
            var parsed = MarkdownParser.Parse(content, title);

            return SnippetComposer.Compose(parsed, settings);
        }
        catch (Exception ex)
        {
            LogComposeFailed(ex, documentId);

            return null;
        }
    }

    /// <summary>
    /// Single-threaded writer, generic over the document type: reconcile snippets (or mark Failed
    /// on a compose error), then stamp the document Indexed.
    /// </summary>
    /// <remarks>
    /// Takes concrete store instances rather than resolving them itself — the caller
    /// (<see cref="RunPipelineAsync{TDocument}"/>) resolves both stores once per pipeline run from
    /// its own write-scoped <c>DbContext</c> and reuses them for every document, since this method
    /// is only ever invoked from the write stage's <c>maxConcurrency: 1</c> reader, which already
    /// guarantees calls here are never concurrent.
    /// </remarks>
    /// <returns><c>true</c> if the document was reconciled and stamped Indexed; <c>false</c> if it was marked Failed.</returns>
    private static async Task<bool> WriteAsync<TDocument>(
        IIndexableDocumentStore<TDocument> docStore,
        ISnippetStore<TDocument> snippetStore,
        TDocument document,
        IReadOnlyList<ComposedSnippet>? snippets,
        CancellationToken cancellationToken
    )
    {
        if (snippets is null)
        {
            await docStore.SetProcessingStatusAsync(
                document,
                DocumentProcessingStatus.Failed,
                cancellationToken
            );

            return false;
        }

        await snippetStore.ReplaceForDocumentAsync(document, snippets, cancellationToken);
        await docStore.SetProcessingStatusAsync(
            document,
            DocumentProcessingStatus.Indexed,
            cancellationToken
        );

        return true;
    }

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Information,
        Message = "⏭️  Snippet indexing sweep is disabled; hosted service will not tick."
    )]
    private partial void LogSweepDisabled();

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Error,
        Message = "❌  Snippet indexing sweep tick failed; will retry next tick."
    )]
    private partial void LogSweepTickFailed(Exception exception);

    [LoggerMessage(
        EventId = 5102,
        Level = LogLevel.Warning,
        Message = "⚠️  Failed to parse/compose snippets for document {DocumentId}; marking Failed."
    )]
    private partial void LogComposeFailed(Exception exception, string documentId);

    [LoggerMessage(
        EventId = 5103,
        Level = LogLevel.Warning,
        Message = "⚠️  Embedding batch of {Count} snippet(s) failed after the SDK's own retries; releasing lease for the next tick."
    )]
    private partial void LogEmbeddingBatchFailed(Exception exception, int count);
}
