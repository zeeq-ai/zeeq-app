namespace Zeeq.Core.Documents;

/// <summary>
/// Domain-slice store contract for the secondary-indexing claim/status lifecycle, generic over the
/// owning document type.
/// </summary>
/// <remarks>
/// <see cref="ILibraryDocumentStore"/> and <see cref="IDocsPublicDocumentStore"/> both close this
/// over their document type and extend it — these two members are the only part of either
/// interface with an identical shape; the rest diverges (library-management-only members on the
/// private side, and different scope-key arity on shared-sounding members like
/// <c>GetByPathAsync</c>/<c>DeleteUnstampedAsync</c>), so only this narrow slice is factored out.
/// </remarks>
/// <typeparam name="TDocument">The owning document type.</typeparam>
public interface IIndexableDocumentStore<TDocument>
{
    /// <summary>
    /// Atomically claims a batch of documents needing secondary indexing and transitions them to
    /// <see cref="DocumentProcessingStatus.Indexing"/>, using a <c>FOR UPDATE SKIP LOCKED</c>
    /// pattern so concurrent worker replicas partition the backlog rather than double-indexing.
    /// </summary>
    /// <remarks>
    /// Claims rows that are <see cref="DocumentProcessingStatus.Pending"/> or
    /// <see cref="DocumentProcessingStatus.Stale"/> (forced re-index), plus
    /// <see cref="DocumentProcessingStatus.Indexing"/> rows older than <paramref name="staleAfter"/>
    /// — the crash-recovery path that reclaims documents abandoned by a worker that died mid-index.
    /// <see cref="DocumentProcessingStatus.Failed"/> rows are never claimed here (a forced retry
    /// re-sets them to Pending).
    /// </remarks>
    /// <param name="limit">Maximum documents to claim in this round.</param>
    /// <param name="staleAfter">Age past which an <c>Indexing</c> row is reclaimable.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The claimed documents, now marked <c>Indexing</c>.</returns>
    Task<IReadOnlyList<TDocument>> ClaimPendingIndexingAsync(
        int limit,
        TimeSpan staleAfter,
        CancellationToken ct
    );

    /// <summary>
    /// Sets the <see cref="DocumentProcessingStatus"/> of a single document (and refreshes its
    /// <c>updated_at</c>), used by the snippet sweep to mark a document
    /// <see cref="DocumentProcessingStatus.Indexed"/> or <see cref="DocumentProcessingStatus.Failed"/>
    /// after processing.
    /// </summary>
    /// <param name="document">The document to update (identifies it by its full key).</param>
    /// <param name="status">The new processing status.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetProcessingStatusAsync(
        TDocument document,
        DocumentProcessingStatus status,
        CancellationToken ct
    );
}
