using Zeeq.Core.Documents;

namespace Zeeq.Platform.Dispatch.Process.Tests;

/// <summary>
/// Minimal in-memory <see cref="IDocsPublicDocumentStore"/> for dispatcher
/// tests. Deliberately duplicated from <c>Zeeq.Platform.Ingest.Tests</c>'s
/// equivalent fake rather than shared across test projects — the fakes are
/// `internal` to their own test assembly and this dispatcher-level test suite
/// only needs enough behavior to prove the dispatcher wires the runner
/// correctly, not to re-verify upsert branching (already covered in
/// <c>Zeeq.Platform.Ingest.Tests</c>).
/// </summary>
internal sealed class FakeDocsPublicDocumentStore : IDocsPublicDocumentStore
{
    public List<DocsPublicDocument> Documents { get; } = [];

    public Task<DocsPublicDocumentUpsertResult> UpsertAsync(
        DocsPublicDocument document,
        CancellationToken ct
    )
    {
        var existing = Documents.SingleOrDefault(row =>
            row.PublicSourceId == document.PublicSourceId && row.Path == document.Path
        );

        if (existing is not null)
        {
            existing.ContentHash = document.ContentHash;
            existing.SyncRunId = document.SyncRunId;
            return Task.FromResult(
                new DocsPublicDocumentUpsertResult(existing, DocumentUpsertKind.Updated)
            );
        }

        Documents.Add(document);
        return Task.FromResult(
            new DocsPublicDocumentUpsertResult(document, DocumentUpsertKind.Added)
        );
    }

    public Task<IReadOnlyList<DocsPublicDocument>> ListBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<DocsPublicDocument>>([
            .. Documents.Where(d => d.PublicSourceId == publicSourceId),
        ]);

    public Task<IReadOnlyList<DocsPublicDocument>> ListSummariesBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    ) => ListBySourceAsync(publicSourceId, ct);

    public Task<DocsPublicDocument?> GetByPathAsync(
        string publicSourceId,
        string path,
        CancellationToken ct
    ) =>
        Task.FromResult(
            Documents.SingleOrDefault(d => d.PublicSourceId == publicSourceId && d.Path == path)
        );

    public Task<int> DeleteUnstampedAsync(
        string publicSourceId,
        string currentSyncRunId,
        CancellationToken ct
    )
    {
        var toRemove = Documents
            .Where(d => d.PublicSourceId == publicSourceId && d.SyncRunId != currentSyncRunId)
            .ToList();

        foreach (var document in toRemove)
        {
            Documents.Remove(document);
        }

        return Task.FromResult(toRemove.Count);
    }

    public Task<IReadOnlyList<DocsPublicDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task SetProcessingStatusAsync(
        DocsPublicDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    ) => throw new NotSupportedException();
}
