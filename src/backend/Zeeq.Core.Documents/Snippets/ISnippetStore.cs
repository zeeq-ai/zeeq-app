namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// Domain-slice store contract for document snippets, generic over the owning document type.
/// </summary>
/// <remarks>
/// Closed over <see cref="LibraryDocument"/> and <see cref="DocsPublicDocument"/> for the private
/// and public snippet stores respectively — the reconciliation contract is identical, only the
/// owning document type (and therefore the scope keys read off it) differs. The embedding-claim
/// and search members below never take or return <typeparamref name="TDocument"/> directly — they
/// operate on snippet ids and table-neutral DTOs — so this interface stays valid under the
/// <c>in TDocument</c> contravariance already established for <see cref="ReplaceForDocumentAsync"/>.
/// </remarks>
/// <typeparam name="TDocument">The owning document type.</typeparam>
public interface ISnippetStore<in TDocument>
{
    /// <summary>
    /// Reconciles the composed snippets for one document against the stored rows in a single
    /// transaction.
    /// </summary>
    /// <remarks>
    /// Matching is by <c>(Kind, ContentHash, Ordinal)</c>: matched rows are kept as-is (preserving
    /// their embedding and embedding model — an unchanged payload never re-embeds); stored rows
    /// with no match are deleted; composed snippets with no match are inserted with a null
    /// embedding (the embedding pipeline picks them up later). Runs in one
    /// <c>SaveChangesAsync</c> so a document's snippet set is always internally consistent.
    /// </remarks>
    /// <param name="document">The owning document (supplies scope keys).</param>
    /// <param name="snippets">The freshly composed snippets for the document.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceForDocumentAsync(
        TDocument document,
        IReadOnlyList<ComposedSnippet> snippets,
        CancellationToken ct
    );

    /// <summary>
    /// Claims up to <paramref name="limit"/> snippets needing embedding via a durable lease.
    /// </summary>
    /// <remarks>
    /// Claims rows where <c>embedding IS NULL</c> or the stored <c>embedding_model</c> stamp does
    /// not match <paramref name="embeddingModel"/> (so a model/dimension change is a self-healing
    /// backfill), and whose <c>embedding_started_at</c> lease is null or older than
    /// <paramref name="lease"/> (crash recovery for an orphaned claim). Uses
    /// <c>FOR UPDATE SKIP LOCKED</c> so concurrent replicas partition the backlog safely. A row
    /// lock cannot span the async embedding provider call, which is why this is a durable lease
    /// column rather than a transaction-scoped lock.
    /// </remarks>
    Task<IReadOnlyList<EmbeddingClaim>> ClaimMissingEmbeddingsAsync(
        string embeddingModel,
        TimeSpan lease,
        int limit,
        CancellationToken ct
    );

    /// <summary>
    /// Writes vectors and the <paramref name="embeddingModel"/> stamp for a batch of claimed
    /// snippets, clearing their embedding-claim lease.
    /// </summary>
    Task SetEmbeddingsAsync(
        IReadOnlyList<EmbeddingResult> results,
        string embeddingModel,
        CancellationToken ct
    );

    /// <summary>
    /// Clears the embedding-claim lease for a batch of snippets without writing vectors.
    /// </summary>
    /// <remarks>
    /// Called when the embedding provider call for a claimed batch fails (after the SDK's own
    /// retries are exhausted — see <c>EmbeddingClientProfile.Batch</c>): releasing the lease lets
    /// the next sweep tick retry immediately rather than waiting out the full lease duration. The
    /// snippet remains full-text-searchable in the meantime.
    /// </remarks>
    Task ReleaseEmbeddingClaimsAsync(IReadOnlyList<string> snippetIds, CancellationToken ct);

    /// <summary>
    /// Runs a single-statement hybrid (RRF-fused vector + full-text) search.
    /// </summary>
    /// <remarks>
    /// When <see cref="SnippetSearchQuery.QueryEmbedding"/> is null, the vector arm is skipped
    /// entirely (full-text-only degraded mode). Uses <c>SET LOCAL hnsw.iterative_scan =
    /// 'relaxed_order'</c> so the org/library/kind/exclusion filters cannot starve the HNSW
    /// candidate walk for a narrow (e.g. small-tenant) scope.
    /// </remarks>
    Task<IReadOnlyList<SnippetSearchRow>> SearchAsync(SnippetSearchQuery query, CancellationToken ct);
}
