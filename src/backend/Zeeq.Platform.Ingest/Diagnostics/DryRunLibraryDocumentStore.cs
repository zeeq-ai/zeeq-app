using Zeeq.Core.Documents;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest.Diagnostics;

/// <summary>
/// Read-through, write-logging decorator over <see cref="ILibraryDocumentStore"/>
/// for dry-running a real private-library ingest against real data with zero DB
/// writes. See <see cref="DryRunDocsPublicDocumentStore"/>'s remarks — same
/// design, mirrored for the library-document side of
/// <see cref="RepositoryIngestRunner"/>.
/// </summary>
/// <remarks>
/// Only the members <c>RepositoryIngestRunner</c> actually calls
/// (<see cref="GetLibraryByIdAsync"/>, <see cref="UpsertSyncedDocumentAsync"/>,
/// <see cref="DeleteUnstampedAsync"/>) are meaningfully implemented; everything
/// else throws, the same way the test fakes do, so an unexpected call surfaces
/// immediately instead of silently no-opping.
/// </remarks>
internal sealed partial class DryRunLibraryDocumentStore : ILibraryDocumentStore
{
    private readonly ILibraryDocumentStore _inner;
    private readonly ILogger<DryRunLibraryDocumentStore> _logger;
    private readonly Dictionary<
        (string OrganizationId, string LibraryId),
        List<LibraryDocument>
    > _snapshots = [];

    public DryRunLibraryDocumentStore(
        ILibraryDocumentStore inner,
        ILogger<DryRunLibraryDocumentStore> logger
    )
    {
        _inner = inner;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) => _inner.GetLibraryByIdAsync(organizationId, libraryId, ct);

    /// <inheritdoc />
    public async Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        var existing = await GetOrLoadSnapshotAsync(
            document.OrganizationId,
            document.LibraryId,
            ct
        );

        var byPath = existing.SingleOrDefault(row => row.Path == document.Path);
        if (byPath is not null)
        {
            var kind =
                byPath.ContentHash == document.ContentHash
                    ? DocumentUpsertKind.Unchanged
                    : DocumentUpsertKind.Updated;
            byPath.ContentHash = document.ContentHash;
            byPath.SyncRunId = document.SyncRunId;
            LogWouldUpsert(document.OrganizationId, document.LibraryId, document.Path, kind);
            return new(byPath, kind);
        }

        var hashMatches = existing
            .Where(row => row.ContentHash == document.ContentHash)
            .Take(2)
            .ToList();
        if (hashMatches.Count == 1)
        {
            var byHash = hashMatches[0];
            LogWouldMove(document.OrganizationId, document.LibraryId, byHash.Path, document.Path);
            byHash.Path = document.Path;
            byHash.SyncRunId = document.SyncRunId;
            return new(byHash, DocumentUpsertKind.Moved);
        }

        existing.Add(document);
        LogWouldUpsert(
            document.OrganizationId,
            document.LibraryId,
            document.Path,
            DocumentUpsertKind.Added
        );
        return new(document, DocumentUpsertKind.Added);
    }

    /// <inheritdoc />
    public async Task<int> DeleteUnstampedAsync(
        string organizationId,
        string libraryId,
        string currentSyncRunId,
        CancellationToken ct
    )
    {
        var existing = await GetOrLoadSnapshotAsync(organizationId, libraryId, ct);
        var toDelete = existing.Where(row => row.SyncRunId != currentSyncRunId).ToList();

        foreach (var document in toDelete)
        {
            LogWouldDelete(organizationId, libraryId, document.Path);
            existing.Remove(document);
        }

        return toDelete.Count;
    }

    private async Task<List<LibraryDocument>> GetOrLoadSnapshotAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    )
    {
        var key = (organizationId, libraryId);
        if (_snapshots.TryGetValue(key, out var snapshot))
        {
            return snapshot;
        }

        // Clone every row — see DryRunDocsPublicDocumentStore's equivalent
        // remark for why this must not depend on the inner store returning
        // fresh instances per call.
        var loaded = (await _inner.ListDocumentsAsync(organizationId, libraryId, ct))
            .Select(Clone)
            .ToList();
        _snapshots[key] = loaded;
        return loaded;
    }

    private static LibraryDocument Clone(LibraryDocument source) =>
        new()
        {
            Id = source.Id,
            OrganizationId = source.OrganizationId,
            TeamId = source.TeamId,
            LibraryId = source.LibraryId,
            Path = source.Path,
            PreviousPaths = source.PreviousPaths,
            Title = source.Title,
            TitleNormalized = source.TitleNormalized,
            Keywords = source.Keywords,
            Headings = source.Headings,
            Content = source.Content,
            ProcessingStatus = source.ProcessingStatus,
            TokenCount = source.TokenCount,
            ContentHash = source.ContentHash,
            SyncRunId = source.SyncRunId,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
        };

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would {Kind} library document. OrganizationId={OrganizationId}, LibraryId={LibraryId}, Path={Path}"
    )]
    private partial void LogWouldUpsert(
        string organizationId,
        string libraryId,
        string path,
        DocumentUpsertKind kind
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would move library document. OrganizationId={OrganizationId}, LibraryId={LibraryId}, From={FromPath}, To={ToPath}"
    )]
    private partial void LogWouldMove(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[DRY RUN] Would delete library document. OrganizationId={OrganizationId}, LibraryId={LibraryId}, Path={Path}"
    )]
    private partial void LogWouldDelete(string organizationId, string libraryId, string path);

    public Task<Library?> GetLibraryAsync(
        string organizationId,
        string name,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
        throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task SetProcessingStatusAsync(
        LibraryDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<Library>> ListLibrariesAsync(
        string organizationId,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
        throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
        throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task DeleteLibraryAsync(string organizationId, string libraryId, CancellationToken ct) =>
        throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<Library> UpdateSyncStateAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<LibraryDocument> UpsertDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task DeleteDocumentAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<LibraryDocument?> GetByPathAsync(
        string organizationId,
        string libraryId,
        string input,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
        string organizationId,
        string libraryId,
        string query,
        int limit,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) => _inner.ListDocumentsAsync(organizationId, libraryId, ct);

    public Task<LibraryDocument?> MoveDocumentAsync(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");

    public Task<LibraryDocument?> SetCodeReviewExclusionAsync(
        string organizationId,
        string libraryId,
        string path,
        bool excluded,
        CancellationToken ct
    ) => throw new NotSupportedException("Dry-run store: not used by RepositoryIngestRunner.");
}
