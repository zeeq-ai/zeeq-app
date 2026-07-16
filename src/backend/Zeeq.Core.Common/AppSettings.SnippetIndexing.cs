namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Snippet secondary-indexing (parse, compose, embed) configuration options.
    /// </summary>
    public SnippetIndexingSettings SnippetIndexing { get; init; } = new();
}

/// <summary>
/// Configuration options for the snippet indexing sweep and embedding pipeline.
/// </summary>
/// <remarks>
/// Consumed by <c>SnippetIndexingHostedService</c> (the sweep) and the snippet stores.
/// Mirrors the style of <see cref="IngestSettings"/>: a plain record of tunables with
/// production-safe defaults. The sweep drains the pending backlog per tick using bounded
/// channels — the concurrency/batch knobs here shape that pipeline (I/O fan-out on the
/// embedding stage, single-threaded DB writes). See the secondary-indexing spec for the
/// reasoning behind each default.
/// </remarks>
public sealed record SnippetIndexingSettings
{
    /// <summary>
    /// Master switch for the sweep. When false the hosted service does not tick.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Sweep tick period, in seconds. Each tick drains the pending backlog.
    /// </summary>
    public int SweepIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Documents claimed per claim round (<c>FOR UPDATE SKIP LOCKED</c>).
    /// </summary>
    public int ClaimBatchSize { get; init; } = 16;

    /// <summary>
    /// Parse/compose stage width (CPU-bound; kept modest).
    /// </summary>
    public int MaxParseConcurrency { get; init; } = 4;

    /// <summary>
    /// Snippets sent per embedding provider call.
    /// </summary>
    public int EmbeddingBatchSize { get; init; } = 64;

    /// <summary>
    /// In-flight embedding provider calls (the I/O fan-out width).
    /// </summary>
    public int MaxEmbeddingConcurrency { get; init; } = 4;

    /// <summary>
    /// Bounded channel capacity applied to each pipeline stage for backpressure.
    /// </summary>
    public int PipelineCapacity { get; init; } = 8;

    /// <summary>
    /// Embedding payload truncation ceiling, in tokens.
    /// </summary>
    public int MaxPayloadTokens { get; init; } = 8_000;

    /// <summary>
    /// Durable embedding-claim lease duration, in minutes. Reclaims orphaned claims
    /// left by a crashed worker (row locks cannot span the async embedding call).
    /// </summary>
    public int EmbeddingLeaseMinutes { get; init; } = 10;

    /// <summary>
    /// Minimum section body length, in characters, to compose a section snippet.
    /// Trivial sections are skipped.
    /// </summary>
    public int MinSectionChars { get; init; } = 80;

    /// <summary>
    /// Per-document snippet cap (defends against pathological documents).
    /// </summary>
    public int MaxSnippetsPerDocument { get; init; } = 500;

    /// <summary>
    /// Age, in minutes, after which an <c>Indexing</c> document is reclaimable — crash recovery.
    /// </summary>
    public int StaleIndexingMinutes { get; init; } = 10;
}
