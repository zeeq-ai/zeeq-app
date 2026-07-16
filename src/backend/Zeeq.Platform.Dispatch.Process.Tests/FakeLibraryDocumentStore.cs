using Zeeq.Core.Documents;

namespace Zeeq.Platform.Dispatch.Process.Tests;

/// <summary>
/// Throwing <see cref="ILibraryDocumentStore"/> stub for dispatcher tests,
/// which only exercise public-source jobs — see
/// <c>FakeDocsPublicDocumentStore</c>'s remarks for why this is a separate
/// per-project fake rather than a shared one.
/// </summary>
internal sealed class FakeLibraryDocumentStore : ILibraryDocumentStore
{
    public Task<Library?> GetLibraryAsync(
        string organizationId,
        string name,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
        throw new NotSupportedException();

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

    public Task<IReadOnlyList<Library>> ListLibrariesAsync(
        string organizationId,
        CancellationToken ct
    ) => throw new NotSupportedException();

    public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    ) => throw new NotSupportedException();

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

    public Task<LibraryDocument> UpsertDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    ) => throw new NotSupportedException();

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
