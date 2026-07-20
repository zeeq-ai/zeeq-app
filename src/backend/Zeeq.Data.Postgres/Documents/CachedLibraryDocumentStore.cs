using Zeeq.Core.Documents;
using Microsoft.Extensions.Caching.Hybrid;

namespace Zeeq.Data.Postgres.Documents;

/// <summary>
/// Caches path-resolution lookups while delegating writes, searches, and library mutations.
/// </summary>
/// <remarks>
/// Path lookups are cached by caller input because exact, suffix, filename, and previous-path
/// aliases can all resolve to the same row. Document writes invalidate all cached path lookups for
/// the containing library so immediate read-after-write flows return fresh content. This cache is
/// L2-only because local in-process entries can survive distributed invalidation timing and make
/// editor saves appear inconsistent.
/// </remarks>
internal sealed class CachedLibraryDocumentStore(ILibraryDocumentStore inner, HybridCache cache)
    : ILibraryDocumentStore
{
    internal static readonly TimeSpan PathCacheTtl = TimeSpan.FromSeconds(60);

    internal static readonly HybridCacheEntryOptions PathCacheOptions = new()
    {
        Expiration = PathCacheTtl,
        Flags = HybridCacheEntryFlags.DisableLocalCache,
    };

    /// <inheritdoc />
    public Task<Library?> GetLibraryAsync(
        string organizationId,
        string name,
        CancellationToken ct
    ) => inner.GetLibraryAsync(organizationId, name, ct);

    /// <inheritdoc />
    public Task<Library?> GetLibraryByIdAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) => inner.GetLibraryByIdAsync(organizationId, libraryId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<Library>> ListLibrariesAsync(
        string organizationId,
        CancellationToken ct
    ) => inner.ListLibrariesAsync(organizationId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<Library>> ListLibrariesByPublicSourceIdAsync(
        string publicSourceId,
        CancellationToken ct
    ) => inner.ListLibrariesByPublicSourceIdAsync(publicSourceId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<Library>> ClaimDueForSyncAsync(int limit, CancellationToken ct) =>
        inner.ClaimDueForSyncAsync(limit, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Pass-through, no caching: claim results are transient work items, never read-after-write
    /// content, so the path-lookup cache does not apply. The claim itself is an atomic UPDATE that
    /// must always hit the database.
    /// </remarks>
    public Task<IReadOnlyList<LibraryDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    ) => inner.ClaimPendingIndexingAsync(limit, staleAfter, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Pass-through, no caching: a status flip does not change any path→document resolution, so
    /// there is nothing in the path-lookup cache to invalidate.
    /// </remarks>
    public Task SetProcessingStatusAsync(
        LibraryDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    ) => inner.SetProcessingStatusAsync(document, status, ct);

    /// <inheritdoc />
    public Task<Library> CreateLibraryAsync(Library library, CancellationToken ct) =>
        inner.CreateLibraryAsync(library, ct);

    /// <inheritdoc />
    public Task<Library> UpdateLibraryAsync(Library library, CancellationToken ct) =>
        inner.UpdateLibraryAsync(library, ct);

    /// <inheritdoc />
    public Task DeleteLibraryAsync(string organizationId, string libraryId, CancellationToken ct) =>
        inner.DeleteLibraryAsync(organizationId, libraryId, ct);

    /// <inheritdoc />
    public Task<Library> UpdateSyncStateAsync(
        string organizationId,
        string libraryId,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        CancellationToken ct
    ) =>
        inner.UpdateSyncStateAsync(
            organizationId,
            libraryId,
            syncStatus,
            nextSyncAt,
            manualTriggerHistory,
            sourceSyncedAt,
            ct
        );

    /// <inheritdoc />
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
    ) =>
        inner.UpdateSyncLeaseAsync(
            organizationId,
            libraryId,
            syncStatus,
            nextSyncAt,
            manualTriggerHistory,
            sourceSyncedAt,
            activeSyncRunId,
            activeSyncRunCreatedAtUtc,
            syncQueuedAtUtc,
            syncStartedAtUtc,
            ct
        );

    /// <inheritdoc />
    public Task<bool> TryUpdateCurrentSyncLeaseAsync(
        string organizationId,
        string libraryId,
        string expectedRunId,
        DateTimeOffset expectedRunCreatedAtUtc,
        string? syncStatus,
        DateTimeOffset? nextSyncAt,
        DateTimeOffset[] manualTriggerHistory,
        DateTimeOffset? sourceSyncedAt,
        string? activeSyncRunId,
        DateTimeOffset? activeSyncRunCreatedAtUtc,
        DateTimeOffset? syncQueuedAtUtc,
        DateTimeOffset? syncStartedAtUtc,
        CancellationToken ct
    ) =>
        inner.TryUpdateCurrentSyncLeaseAsync(
            organizationId,
            libraryId,
            expectedRunId,
            expectedRunCreatedAtUtc,
            syncStatus,
            nextSyncAt,
            manualTriggerHistory,
            sourceSyncedAt,
            activeSyncRunId,
            activeSyncRunCreatedAtUtc,
            syncQueuedAtUtc,
            syncStartedAtUtc,
            ct
        );

    /// <inheritdoc />
    public Task<LibrarySyncStateReset?> ResetLibrarySyncStateAsync(
        string organizationId,
        string libraryId,
        DateTimeOffset now,
        CancellationToken ct
    ) => inner.ResetLibrarySyncStateAsync(organizationId, libraryId, now, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<StalledSyncReset>> ResetStalledSyncsAsync(
        DateTimeOffset now,
        TimeSpan queuedStaleAfter,
        TimeSpan runningStaleAfter,
        int limit,
        CancellationToken ct
    ) => inner.ResetStalledSyncsAsync(now, queuedStaleAfter, runningStaleAfter, limit, ct);

    /// <inheritdoc />
    public async Task<LibraryDocument> UpsertDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        var saved = await inner.UpsertDocumentAsync(document, ct);
        await cache.RemoveByTagAsync(
            LibraryPathCacheTag(saved.OrganizationId, saved.LibraryId),
            ct
        );

        return saved;
    }

    /// <inheritdoc />
    public async Task<LibraryDocumentUpsertResult> UpsertSyncedDocumentAsync(
        LibraryDocument document,
        CancellationToken ct
    )
    {
        var result = await inner.UpsertSyncedDocumentAsync(document, ct);
        await cache.RemoveByTagAsync(
            LibraryPathCacheTag(result.Document.OrganizationId, result.Document.LibraryId),
            ct
        );

        return result;
    }

    /// <inheritdoc />
    public async Task<int> DeleteUnstampedAsync(
        string organizationId,
        string libraryId,
        string currentSyncRunId,
        CancellationToken ct
    )
    {
        var deleted = await inner.DeleteUnstampedAsync(
            organizationId,
            libraryId,
            currentSyncRunId,
            ct
        );
        await cache.RemoveByTagAsync(LibraryPathCacheTag(organizationId, libraryId), ct);

        return deleted;
    }

    /// <inheritdoc />
    public async Task DeleteDocumentAsync(
        string organizationId,
        string libraryId,
        string path,
        CancellationToken ct
    )
    {
        await inner.DeleteDocumentAsync(organizationId, libraryId, path, ct);
        await cache.RemoveByTagAsync(LibraryPathCacheTag(organizationId, libraryId), ct);
    }

    /// <inheritdoc />
    public async Task<LibraryDocument?> GetByPathAsync(
        string organizationId,
        string libraryId,
        string input,
        CancellationToken ct
    )
    {
        var normalizedInput = DocumentNormalizer.NormalizePath(input);
        var cacheKey = PathCacheKey(organizationId, libraryId, normalizedInput);
        var result = await cache.GetOrCreateAsync<CacheState, LibraryDocument?>(
            cacheKey,
            new(inner, organizationId, libraryId, normalizedInput),
            static async (state, cancellationToken) =>
                await state.Store.GetByPathAsync(
                    state.OrganizationId,
                    state.LibraryId,
                    state.NormalizedInput,
                    cancellationToken
                ),
            PathCacheOptions,
            [LibraryPathCacheTag(organizationId, libraryId)],
            cancellationToken: ct
        );

        if (result is null)
        {
            await cache.RemoveAsync(cacheKey, ct);
        }

        return result;
    }

    /// <inheritdoc />
    public Task<LibraryDocument?> GetByIdAsync(
        string organizationId,
        string libraryId,
        string documentId,
        CancellationToken ct
    ) => inner.GetByIdAsync(organizationId, libraryId, documentId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<LibraryDocumentMatch>> SearchAsync(
        string organizationId,
        string libraryId,
        string query,
        int limit,
        CancellationToken ct
    ) => inner.SearchAsync(organizationId, libraryId, query, limit, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<LibraryDocument>> ListDocumentsAsync(
        string organizationId,
        string libraryId,
        CancellationToken ct
    ) => inner.ListDocumentsAsync(organizationId, libraryId, ct);

    /// <inheritdoc />
    public async Task<LibraryDocument?> MoveDocumentAsync(
        string organizationId,
        string libraryId,
        string fromPath,
        string toPath,
        CancellationToken ct
    )
    {
        var moved = await inner.MoveDocumentAsync(organizationId, libraryId, fromPath, toPath, ct);
        if (moved is not null)
        {
            await cache.RemoveByTagAsync(LibraryPathCacheTag(organizationId, libraryId), ct);
        }

        return moved;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Invalidates the library's path-lookup cache on success: cached <see cref="GetByPathAsync"/>
    /// entities carry <see cref="LibraryDocument.ExcludedFromCodeReviews"/>, and the editor
    /// re-reads the document right after toggling — a stale cached flag would make the toggle
    /// appear to not stick.
    /// </remarks>
    public async Task<LibraryDocument?> SetCodeReviewExclusionAsync(
        string organizationId,
        string libraryId,
        string documentId,
        bool excluded,
        CancellationToken ct
    )
    {
        var updated = await inner.SetCodeReviewExclusionAsync(
            organizationId,
            libraryId,
            documentId,
            excluded,
            ct
        );

        if (updated is not null)
        {
            await cache.RemoveByTagAsync(LibraryPathCacheTag(organizationId, libraryId), ct);
        }

        return updated;
    }

    private static string PathCacheKey(
        string organizationId,
        string libraryId,
        string normalizedInput
    ) => $"doc:path:{organizationId}:{libraryId}:{normalizedInput}";

    private static string LibraryPathCacheTag(string organizationId, string libraryId) =>
        $"doc:path:library:{organizationId}:{libraryId}";

    private sealed record CacheState(
        ILibraryDocumentStore Store,
        string OrganizationId,
        string LibraryId,
        string NormalizedInput
    );
}
