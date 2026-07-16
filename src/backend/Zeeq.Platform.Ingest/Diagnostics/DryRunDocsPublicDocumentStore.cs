using Zeeq.Core.Documents;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest.Diagnostics;

/// <summary>
/// Read-through, write-logging decorator over <see cref="IDocsPublicDocumentStore"/>
/// for dry-running a real ingest against real data with zero DB writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never register this in the app's DI container.</b> It exists purely for
/// interactive dry runs via the <c>zeeq-dotnet-repl</c> skill — construct it
/// directly (typically through <see cref="DryRunIngestRunnerFactory"/>) against
/// the real, DI-resolved <see cref="IDocsPublicDocumentStore"/> inside a running
/// process, so <see cref="RepositoryIngestRunner"/> can execute a real git clone
/// (via the real <c>IIngestWorkspaceProvider</c>) and real parse/hash pass, while
/// every would-be write is only logged.
/// </para>
/// <para>
/// Reads are real (delegated to <c>inner</c>) so move-detection is evaluated
/// against actual current state, not an empty simulation — a source with
/// existing documents correctly reports <c>Updated</c>/<c>Moved</c>/<c>Unchanged</c>,
/// not everything showing as <c>Added</c>. Existing documents for the source are
/// snapshotted once (<see cref="IDocsPublicDocumentStore.ListBySourceAsync"/>)
/// at construction and mutated only in this in-memory copy, mirroring the same
/// four-way branch <c>PostgresDocsPublicDocumentStore.UpsertAsync</c> uses —
/// see that method's remarks for why <c>Take(2)</c> resolves an ambiguous
/// content-hash match to <c>Added</c> rather than throwing or guessing.
/// </para>
/// </remarks>
internal sealed partial class DryRunDocsPublicDocumentStore : IDocsPublicDocumentStore
{
    private readonly IDocsPublicDocumentStore _inner;
    private readonly ILogger<DryRunDocsPublicDocumentStore> _logger;
    private readonly Dictionary<string, List<DocsPublicDocument>> _snapshotBySource = [];

    public DryRunDocsPublicDocumentStore(
        IDocsPublicDocumentStore inner,
        ILogger<DryRunDocsPublicDocumentStore> logger
    )
    {
        _inner = inner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DocsPublicDocumentUpsertResult> UpsertAsync(
        DocsPublicDocument document,
        CancellationToken ct
    )
    {
        var existing = await GetOrLoadSnapshotAsync(document.PublicSourceId, ct);

        var byPath = existing.SingleOrDefault(row => row.Path == document.Path);
        if (byPath is not null)
        {
            var kind =
                byPath.ContentHash == document.ContentHash
                    ? DocumentUpsertKind.Unchanged
                    : DocumentUpsertKind.Updated;
            byPath.ContentHash = document.ContentHash;
            byPath.SyncRunId = document.SyncRunId;
            LogWouldUpsert(document.PublicSourceId, document.Path, kind);
            return new(byPath, kind);
        }

        var hashMatches = existing.Where(row => row.ContentHash == document.ContentHash).Take(2).ToList();
        if (hashMatches.Count == 1)
        {
            var byHash = hashMatches[0];
            LogWouldMove(document.PublicSourceId, byHash.Path, document.Path);
            byHash.Path = document.Path;
            byHash.SyncRunId = document.SyncRunId;
            return new(byHash, DocumentUpsertKind.Moved);
        }

        existing.Add(document);
        LogWouldUpsert(document.PublicSourceId, document.Path, DocumentUpsertKind.Added);
        return new(document, DocumentUpsertKind.Added);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DocsPublicDocument>> ListBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) => _inner.ListBySourceAsync(publicSourceId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<DocsPublicDocument>> ListSummariesBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) => _inner.ListSummariesBySourceAsync(publicSourceId, ct);

    /// <inheritdoc />
    public Task<DocsPublicDocument?> GetByPathAsync(
        string publicSourceId,
        string path,
        CancellationToken ct
    ) => _inner.GetByPathAsync(publicSourceId, path, ct);

    public Task<IReadOnlyList<DocsPublicDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task SetProcessingStatusAsync(
        DocsPublicDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    /// <inheritdoc />
    public async Task<int> DeleteUnstampedAsync(
        string publicSourceId,
        string currentSyncRunId,
        CancellationToken ct
    )
    {
        var existing = await GetOrLoadSnapshotAsync(publicSourceId, ct);
        var toDelete = existing.Where(row => row.SyncRunId != currentSyncRunId).ToList();

        foreach (var document in toDelete)
        {
            LogWouldDelete(publicSourceId, document.Path);
            existing.Remove(document);
        }

        return toDelete.Count;
    }

    private async Task<List<DocsPublicDocument>> GetOrLoadSnapshotAsync(
        string publicSourceId,
        CancellationToken ct
    )
    {
        if (_snapshotBySource.TryGetValue(publicSourceId, out var snapshot))
        {
            return snapshot;
        }

        // Clone every row rather than keeping the inner store's returned
        // instances directly — mutating those in place (the whole point of
        // simulating a move/update) must never risk touching real state.
        // Postgres's real store always returns fresh AsNoTracking() instances
        // per call anyway, but this must not depend on that being true of
        // every possible inner store.
        var loaded = (await _inner.ListBySourceAsync(publicSourceId, ct))
            .Select(Clone)
            .ToList();
        _snapshotBySource[publicSourceId] = loaded;
        return loaded;
    }

    private static DocsPublicDocument Clone(DocsPublicDocument source) =>
        new()
        {
            Id = source.Id,
            PublicSourceId = source.PublicSourceId,
            Path = source.Path,
            PreviousPaths = source.PreviousPaths,
            Title = source.Title,
            TitleNormalized = source.TitleNormalized,
            Keywords = source.Keywords,
            Headings = source.Headings,
            Content = source.Content,
            ContentHash = source.ContentHash,
            TokenCount = source.TokenCount,
            ProcessingStatus = source.ProcessingStatus,
            SyncRunId = source.SyncRunId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
        };

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would {Kind} public document. PublicSourceId={PublicSourceId}, Path={Path}"
    )]
    private partial void LogWouldUpsert(string publicSourceId, string path, DocumentUpsertKind kind);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would move public document. PublicSourceId={PublicSourceId}, From={FromPath}, To={ToPath}"
    )]
    private partial void LogWouldMove(string publicSourceId, string fromPath, string toPath);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would delete public document. PublicSourceId={PublicSourceId}, Path={Path}"
    )]
    private partial void LogWouldDelete(string publicSourceId, string path);
}
