using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// In-memory <see cref="ILibraryDocumentStore"/> covering just what the
/// manual-trigger handler tests need — library lookup and sync-state updates.
/// Every other member throws, so an accidental call surfaces immediately
/// instead of silently returning a default value.
/// </summary>
internal sealed class FakeLibraryDocumentStore : ILibraryDocumentStore
{
    public List<Library> Libraries { get; } = [];

    /// <summary>
    /// In-memory documents, mirroring <see cref="FakeDocsPublicDocumentStore.Documents"/>
    /// so <c>RepositoryIngestRunner</c>'s private branch is exercised the same
    /// way its public branch already is.
    /// </summary>
    public List<LibraryDocument> Documents { get; } = [];

    public Task<Library?> GetLibraryAsync(
        string organizationId,
        string name,
        CancellationToken ct
    ) =>
        Task.FromResult(
            Libraries.SingleOrDefault(l => l.OrganizationId == organizationId && l.Name == name)
        );

    public Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) =>
        Task.FromResult(
            Libraries.SingleOrDefault(l => l.OrganizationId == organizationId && l.Id == libraryId)
        );

    public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Library>> ClaimDueNotionSyncAsync(
        int limit,
        DateTimeOffset now,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<LibrarySyncStateReset?> ResetLibrarySyncStateAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var library = Libraries.SingleOrDefault(l =>
            l.OrganizationId == organizationId
            && l.Id == libraryId
            && l.SourceKind is not null
            && l.SyncStatus is "queued" or "running"
            && l.ActiveSyncRunId is not null
            && l.ActiveSyncRunCreatedAtUtc is not null
        );
        if (library is null)
        {
            return Task.FromResult<LibrarySyncStateReset?>(null);
        }

        var cleared = new StalledSyncReset(
            library.SourceKind == RepositorySourceKind.Notion.ToString()
                ? RepositorySourceKind.Notion
                : RepositorySourceKind.Private,
            library.Id,
            library.OrganizationId,
            library.Id,
            library.ActiveSyncRunId,
            library.ActiveSyncRunCreatedAtUtc
        );
        library.SyncStatus = "idle";
        library.NextSyncAt = now;
        library.ActiveSyncRunId = null;
        library.ActiveSyncRunCreatedAtUtc = null;
        library.SyncQueuedAtUtc = null;
        library.SyncStartedAtUtc = null;

        return Task.FromResult<LibrarySyncStateReset?>(new LibrarySyncStateReset(library, cleared));
    }

    public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task SetProcessingStatusAsync(
        LibraryDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<Library> UpdateSyncStateAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        CancellationToken ct
    )
    {
        var library = Libraries.Single(l =>
            l.OrganizationId == organizationId && l.Id == libraryId
        );
        library.SyncStatus = syncStatus;
        library.NextSyncAt = nextSyncAt;
        library.ManualTriggerHistory = manualTriggerHistory;
        library.SourceSyncedAt = sourceSyncedAt;

        return Task.FromResult(library);
    }

    public Task<Library> UpdateSyncLeaseAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        CancellationToken ct
    )
    {
        var library = Libraries.Single(l =>
            l.OrganizationId == organizationId && l.Id == libraryId
        );
        library.SyncStatus = syncStatus;
        library.NextSyncAt = nextSyncAt;
        library.ManualTriggerHistory = manualTriggerHistory;
        library.SourceSyncedAt = sourceSyncedAt;
        library.ActiveSyncRunId = activeSyncRunId;
        library.ActiveSyncRunCreatedAtUtc = activeSyncRunCreatedAtUtc;
        library.SyncQueuedAtUtc = syncQueuedAtUtc;
        library.SyncStartedAtUtc = syncStartedAtUtc;

        return Task.FromResult(library);
    }

    public Task<bool> TryUpdateCurrentSyncLeaseAsync(
        string organizationId,
        string libraryId,
        string expectedRunId,
        DateTimeOffset expectedRunCreatedAtUtc,
        string? expectedSyncStatus,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        DateTimeOffset? nextFullResyncAt,
        CancellationToken ct
    )
    {
        var library = Libraries.Single(l =>
            l.OrganizationId == organizationId && l.Id == libraryId
        );
        if (
            library.ActiveSyncRunId != expectedRunId
            || library.ActiveSyncRunCreatedAtUtc != expectedRunCreatedAtUtc
            || (expectedSyncStatus is not null && library.SyncStatus != expectedSyncStatus)
        )
        {
            return Task.FromResult(false);
        }

        library.SyncStatus = syncStatus;
        library.NextSyncAt = nextSyncAt;
        library.ManualTriggerHistory = manualTriggerHistory;
        library.SourceSyncedAt = sourceSyncedAt;
        library.ActiveSyncRunId = activeSyncRunId;
        library.ActiveSyncRunCreatedAtUtc = activeSyncRunCreatedAtUtc;
        library.SyncQueuedAtUtc = syncQueuedAtUtc;
        library.SyncStartedAtUtc = syncStartedAtUtc;
        library.NextFullResyncAt = nextFullResyncAt;

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Library>> ListLibrariesAsync(
        string organizationId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    ) =>
        Task.FromResult<IReadOnlyList<Library>>([
            .. Libraries.Where(l => l.PublicSourceId == publicSourceId),
        ]);

    public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task DeleteLibraryAsync(string organizationId, string libraryId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<LibraryDocument> UpsertDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    ) => throw new NotSupportedException();

    /// <summary>Mirrors <see cref="FakeDocsPublicDocumentStore.UpsertAsync"/>'s branching exactly.</summary>
    public Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        if (document.SourceExternalId is not null)
        {
            var byExternalId = Documents.SingleOrDefault(row =>
                row.OrganizationId == document.OrganizationId
                && row.LibraryId == document.LibraryId
                && row.SourceExternalId == document.SourceExternalId
            );
            if (byExternalId is not null)
            {
                var moved = byExternalId.Path != document.Path;
                var changed = byExternalId.ContentHash != document.ContentHash;
                if (moved)
                {
                    byExternalId.PreviousPaths = byExternalId.PreviousPaths.Contains(
                        byExternalId.Path
                    )
                        ? byExternalId.PreviousPaths
                        : [.. byExternalId.PreviousPaths, byExternalId.Path];
                    byExternalId.Path = document.Path;
                }

                if (changed)
                {
                    CopyDocumentFields(byExternalId, document);
                }

                byExternalId.SyncRunId = document.SyncRunId;
                byExternalId.UpdatedAt = document.UpdatedAt;
                return Task.FromResult(
                    new LibraryDocumentUpsertResult(
                        byExternalId,
                        moved ? DocumentUpsertKind.Moved
                            : changed ? DocumentUpsertKind.Updated
                            : DocumentUpsertKind.Unchanged
                    )
                );
            }
        }

        var byPath = Documents.SingleOrDefault(row =>
            row.OrganizationId == document.OrganizationId
            && row.LibraryId == document.LibraryId
            && row.Path == document.Path
        );

        if (byPath is not null)
        {
            if (byPath.ContentHash == document.ContentHash)
            {
                byPath.SyncRunId = document.SyncRunId;
                return Task.FromResult(
                    new LibraryDocumentUpsertResult(byPath, DocumentUpsertKind.Unchanged)
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
                new LibraryDocumentUpsertResult(byPath, DocumentUpsertKind.Updated)
            );
        }

        var hashMatches = Documents
            .Where(row =>
                row.OrganizationId == document.OrganizationId
                && row.LibraryId == document.LibraryId
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
                new LibraryDocumentUpsertResult(byHash, DocumentUpsertKind.Moved)
            );
        }

        Documents.Add(document);
        return Task.FromResult(new LibraryDocumentUpsertResult(document, DocumentUpsertKind.Added));
    }

    public Task<int> DeleteUnstampedAsync(
        string organizationId,
        string libraryId,
        string currentSyncRunId,
        CancellationToken ct
    )
    {
        var toRemove = Documents
            .Where(d =>
                d.OrganizationId == organizationId
                && d.LibraryId == libraryId
                && d.SyncRunId != currentSyncRunId
            )
            .ToList();

        foreach (var document in toRemove)
        {
            Documents.Remove(document);
        }

        return Task.FromResult(toRemove.Count);
    }

    public Task DeleteDocumentAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task DeleteDocumentByExternalIdAsync(
        string organizationId,
        string libraryId,
        string sourceExternalId,
        CancellationToken ct
    )
    {
        Documents.RemoveAll(row =>
            row.OrganizationId == organizationId
            && row.LibraryId == libraryId
            && row.SourceExternalId == sourceExternalId
        );
        return Task.CompletedTask;
    }

    public Task<LibraryDocument?> GetByPathAsync(
        string organizationId,
        string libraryId,
        string input,
        CancellationToken ct
    ) =>
        Task.FromResult(
            Documents.SingleOrDefault(row =>
                row.OrganizationId == organizationId
                && row.LibraryId == libraryId
                && row.Path == input
            )
        );

    public Task<LibraryDocument?> GetByExternalIdAsync(
        string organizationId,
        string libraryId,
        string sourceExternalId,
        CancellationToken ct
    ) =>
        Task.FromResult(
            Documents.SingleOrDefault(row =>
                row.OrganizationId == organizationId
                && row.LibraryId == libraryId
                && row.SourceExternalId == sourceExternalId
            )
        );

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

    private static void CopyDocumentFields(LibraryDocument target, LibraryDocument source)
    {
        target.Content = source.Content;
        target.ContentHash = source.ContentHash;
        target.Title = source.Title;
        target.TitleNormalized = source.TitleNormalized;
        target.ParsedSkillName = source.ParsedSkillName;
        target.ParsedSkillDescription = source.ParsedSkillDescription;
        target.Keywords = source.Keywords;
        target.Headings = source.Headings;
        target.TokenCount = source.TokenCount;
        target.ProcessingStatus = source.ProcessingStatus;
    }
}
