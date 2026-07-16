using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// In-memory <see cref="IDocsPublicDocumentStore"/> mirroring the Postgres
/// implementation's upsert/sweep semantics, so runner tests exercise the same
/// branching without a database.
/// </summary>
internal sealed class FakeDocsPublicDocumentStore : IDocsPublicDocumentStore
{
    public List<DocsPublicDocument> Documents { get; } = [];

    public Task<DocsPublicDocumentUpsertResult> UpsertAsync(
        DocsPublicDocument document,
        CancellationToken ct
    )
    {
        var byPath = Documents.SingleOrDefault(row =>
            row.PublicSourceId == document.PublicSourceId && row.Path == document.Path
        );

        if (byPath is not null)
        {
            if (byPath.ContentHash == document.ContentHash)
            {
                byPath.SyncRunId = document.SyncRunId;
                return Task.FromResult(
                    new DocsPublicDocumentUpsertResult(byPath, DocumentUpsertKind.Unchanged)
                );
            }

            byPath.Content = document.Content;
            byPath.ContentHash = document.ContentHash;
            byPath.Title = document.Title;
            byPath.TitleNormalized = document.TitleNormalized;
            byPath.Keywords = document.Keywords;
            byPath.Headings = document.Headings;
            byPath.TokenCount = document.TokenCount;
            byPath.ProcessingStatus = document.ProcessingStatus;
            byPath.SyncRunId = document.SyncRunId;
            byPath.UpdatedAt = document.UpdatedAt;
            return Task.FromResult(
                new DocsPublicDocumentUpsertResult(byPath, DocumentUpsertKind.Updated)
            );
        }

        var hashMatches = Documents
            .Where(row =>
                row.PublicSourceId == document.PublicSourceId
                && row.ContentHash == document.ContentHash
            )
            .Take(2)
            .ToList();

        if (hashMatches.Count == 1)
        {
            var byHash = hashMatches[0];
            byHash.PreviousPaths = byHash.PreviousPaths.Contains(byHash.Path)
                ? byHash.PreviousPaths
                : [.. byHash.PreviousPaths, byHash.Path];
            byHash.Path = document.Path;
            byHash.SyncRunId = document.SyncRunId;
            byHash.UpdatedAt = document.UpdatedAt;
            return Task.FromResult(
                new DocsPublicDocumentUpsertResult(byHash, DocumentUpsertKind.Moved)
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
            .. Documents.Where(d => d.PublicSourceId == publicSourceId).OrderBy(d => d.Path),
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
    ) =>
        Task.FromResult(
            Documents.RemoveAll(d =>
                d.PublicSourceId == publicSourceId && d.SyncRunId != currentSyncRunId
            )
        );

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
