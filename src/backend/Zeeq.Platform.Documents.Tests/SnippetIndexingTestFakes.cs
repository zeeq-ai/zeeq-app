using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Snippets;

namespace Zeeq.Platform.Documents.Tests;

/// <summary>
/// In-memory <see cref="ILibraryDocumentStore"/> fake for sweep orchestration tests. Only the
/// members the sweep calls (<see cref="ClaimPendingIndexingAsync"/>, <see cref="SetProcessingStatusAsync"/>)
/// are functional; everything else throws so an unexpected call surfaces immediately.
/// </summary>
internal sealed class FakeLibraryDocumentStore(IReadOnlyList<LibraryDocument> documents)
    : ILibraryDocumentStore
{
    private readonly List<LibraryDocument> _documents = [.. documents];

    /// <summary>Terminal processing status recorded per document id.</summary>
    public Dictionary<string, DocumentProcessingStatus> StatusById { get; } = [];

    /// <summary>Forces every seeded document to Indexed (used to simulate a drained backlog).</summary>
    public void MarkAllIndexed()
    {
        foreach (var document in _documents)
            document.ProcessingStatus = DocumentProcessingStatus.Indexed;
    }

    public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    )
    {
        var claimed = _documents
            .Where(d => d.ProcessingStatus == DocumentProcessingStatus.Pending)
            .Take(limit)
            .ToArray();

        foreach (var document in claimed)
            document.ProcessingStatus = DocumentProcessingStatus.Indexing;

        return Task.FromResult<IReadOnlyList<LibraryDocument>>(claimed);
    }

    public Task SetProcessingStatusAsync(
        LibraryDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    )
    {
        StatusById[document.Id] = status;

        var tracked = _documents.SingleOrDefault(d => d.Id == document.Id);
        if (tracked is not null)
            tracked.ProcessingStatus = status;

        return Task.CompletedTask;
    }

    public Task<Library?> GetLibraryAsync(string organizationId, string name, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<Library>> ListLibrariesAsync(
        string organizationId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task DeleteLibraryAsync(string organizationId, string libraryId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Library> UpdateSyncStateAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<LibraryDocument> UpsertDocumentAsync(LibraryDocument document, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<int> DeleteUnstampedAsync(
        string organizationId,
        string libraryId,
        string currentSyncRunId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task DeleteDocumentAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<LibraryDocument?> GetByPathAsync(
        string organizationId,
        string libraryId,
        string input,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
        string organizationId,
        string libraryId,
        string query,
        int limit,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<LibraryDocument?> MoveDocumentAsync(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
        string organizationId,
        string libraryId,
        string path,
        bool excluded,
        CancellationToken ct
    ) => throw new NotSupportedException();
}

/// <summary>
/// One in-memory snippet row, tracking the embedding-relevant fields the real entity has so
/// <see cref="FakeLibraryDocumentSnippetStore"/> can exercise the full claim/embed/release/search
/// contract, not just reconciliation.
/// </summary>
internal sealed class FakeSnippetRow
{
    public required string Id { get; init; }
    public required string DocumentId { get; init; }
    public required string EmbeddingPayload { get; init; }
    public Pgvector.HalfVector? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public DateTimeOffset? EmbeddingStartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// In-memory <see cref="ISnippetStore{TDocument}"/> fake that records the reconciled snippet
/// set per document and supports the embedding-claim lease contract, for Pipeline B tests.
/// </summary>
internal sealed class FakeLibraryDocumentSnippetStore : ISnippetStore<LibraryDocument>
{
    private readonly List<FakeSnippetRow> _rows = [];
    private int _nextId;

    /// <summary>Composed snippets last reconciled for each document id.</summary>
    public Dictionary<string, IReadOnlyList<ComposedSnippet>> SnippetsByDocument { get; } = [];

    /// <summary>All rows currently tracked (read-only view for test assertions).</summary>
    public IReadOnlyList<FakeSnippetRow> Rows => _rows;

    /// <summary>
    /// Gated hook for concurrency/backpressure tests: when set, every claimed batch awaits this
    /// task before <see cref="ClaimMissingEmbeddingsAsync"/> returns, letting a test block claims
    /// until it is ready to release them.
    /// </summary>
    public Func<Task>? OnClaim { get; set; }

    /// <summary>Records the highest number of rows ever claimed concurrently (unreleased/unset).</summary>
    public int MaxConcurrentClaims { get; private set; }

    // Tracks currently-claimed ids (not a raw counter) so a duplicate release or duplicate
    // SetEmbeddings call for the same id is a no-op instead of corrupting the active count —
    // hardened per code review follow-up (2026-07-11): the real store's lease is a durable
    // column keyed by id, so the fake's accounting should be equally idempotent per id.
    private readonly HashSet<string> _activeClaimIds = [];
    private readonly Lock _activeClaimIdsLock = new();

    public Task ReplaceForDocumentAsync(
        LibraryDocument document,
        IReadOnlyList<ComposedSnippet> snippets,
        CancellationToken ct
    )
    {
        SnippetsByDocument[document.Id] = snippets;

        foreach (var composed in snippets)
        {
            _rows.Add(
                new FakeSnippetRow
                {
                    Id = $"fake_snip_{Interlocked.Increment(ref _nextId)}",
                    DocumentId = document.Id,
                    EmbeddingPayload = composed.EmbeddingPayload,
                }
            );
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<EmbeddingClaim>> ClaimMissingEmbeddingsAsync(
        string embeddingModel,
        TimeSpan lease,
        int limit,
        CancellationToken ct
    )
    {
        var now = DateTimeOffset.UtcNow;
        var leaseCutoff = now - lease;

        var claimable = _rows
            .Where(row =>
                (row.Embedding is null || row.EmbeddingModel != embeddingModel)
                && (row.EmbeddingStartedAt is null || row.EmbeddingStartedAt < leaseCutoff)
            )
            .Take(limit)
            .ToArray();

        foreach (var row in claimable)
            row.EmbeddingStartedAt = now;

        lock (_activeClaimIdsLock)
        {
            foreach (var row in claimable)
                _activeClaimIds.Add(row.Id);

            MaxConcurrentClaims = Math.Max(MaxConcurrentClaims, _activeClaimIds.Count);
        }

        if (OnClaim is { } onClaim)
            await onClaim();

        return claimable.Select(row => new EmbeddingClaim(row.Id, row.EmbeddingPayload)).ToArray();
    }

    public Task SetEmbeddingsAsync(
        IReadOnlyList<EmbeddingResult> results,
        string embeddingModel,
        CancellationToken ct
    )
    {
        foreach (var result in results)
        {
            var row = _rows.Single(r => r.Id == result.Id);
            row.Embedding = result.Embedding;
            row.EmbeddingModel = embeddingModel;
            row.EmbeddingStartedAt = null;
            row.UpdatedAt = DateTimeOffset.UtcNow;

            lock (_activeClaimIdsLock)
            {
                // Set.Remove is idempotent — a duplicate SetEmbeddings call for an id already
                // written (or never claimed) does not corrupt the active-claim count.
                _activeClaimIds.Remove(result.Id);
            }
        }

        return Task.CompletedTask;
    }

    public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<string> snippetIds, CancellationToken ct)
    {
        foreach (var id in snippetIds)
        {
            var row = _rows.SingleOrDefault(r => r.Id == id);
            if (row is null)
                continue;

            row.EmbeddingStartedAt = null;

            lock (_activeClaimIdsLock)
            {
                // Idempotent for the same reason as SetEmbeddingsAsync: releasing an id twice
                // (or an id never claimed) is a safe no-op, not a corrupted count.
                _activeClaimIds.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SnippetSearchRow>> SearchAsync(
        SnippetSearchQuery query,
        CancellationToken ct
    ) => throw new NotSupportedException("Not exercised by SnippetIndexingHostedService tests.");
}

/// <summary>Empty <see cref="IDocsPublicDocumentStore"/>: the public claim always returns nothing.</summary>
internal sealed class EmptyPublicDocumentStore : IDocsPublicDocumentStore
{
    public Task<IReadOnlyList<DocsPublicDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    ) => Task.FromResult<IReadOnlyList<DocsPublicDocument>>([]);

    public Task SetProcessingStatusAsync(
        DocsPublicDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    ) => Task.CompletedTask;

    public Task<DocsPublicDocumentUpsertResult> UpsertAsync(
        DocsPublicDocument document,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<DocsPublicDocument>> ListBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<DocsPublicDocument>> ListSummariesBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<DocsPublicDocument?> GetByPathAsync(
        string publicSourceId,
        string path,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<int> DeleteUnstampedAsync(
        string publicSourceId,
        string currentSyncRunId,
        CancellationToken ct
    ) => throw new NotSupportedException();
}

/// <summary>No-op <see cref="ISnippetStore{TDocument}"/>: never exercised (public claim is empty).</summary>
internal sealed class NoOpPublicSnippetStore : ISnippetStore<DocsPublicDocument>
{
    public Task ReplaceForDocumentAsync(
        DocsPublicDocument document,
        IReadOnlyList<ComposedSnippet> snippets,
        CancellationToken ct
    ) => Task.CompletedTask;

    public Task<IReadOnlyList<EmbeddingClaim>> ClaimMissingEmbeddingsAsync(
        string embeddingModel,
        TimeSpan lease,
        int limit,
        CancellationToken ct
    ) => Task.FromResult<IReadOnlyList<EmbeddingClaim>>([]);

    public Task SetEmbeddingsAsync(
        IReadOnlyList<EmbeddingResult> results,
        string embeddingModel,
        CancellationToken ct
    ) => Task.CompletedTask;

    public Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<string> snippetIds, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<SnippetSearchRow>> SearchAsync(
        SnippetSearchQuery query,
        CancellationToken ct
    ) => throw new NotSupportedException("Not exercised by SnippetIndexingHostedService tests.");
}
