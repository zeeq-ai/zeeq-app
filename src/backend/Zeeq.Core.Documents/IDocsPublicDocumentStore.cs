namespace Zeeq.Core.Documents;

/// <summary>
/// Store contract for documents ingested from public repository sources.
/// </summary>
public interface IDocsPublicDocumentStore : IIndexableDocumentStore<DocsPublicDocument>
{
    /// <summary>
    /// Inserts, updates, or move-detects a document within one public source.
    /// </summary>
    /// <remarks>
    /// Resolution order, scoped to <see cref="DocsPublicDocument.PublicSourceId"/>
    /// so cross-source content-hash collisions never merge documents:
    /// <list type="number">
    ///   <item>Exact <c>(public_source_id, path)</c> match with an unchanged
    ///   <c>content_hash</c> → re-stamp <c>sync_run_id</c> only; <c>updated_at</c>
    ///   does not advance. Result: <see cref="DocumentUpsertKind.Unchanged"/>.</item>
    ///   <item>Exact path match with a changed hash → update content fields,
    ///   re-stamp, advance <c>updated_at</c>. Result: <see cref="DocumentUpsertKind.Updated"/>.</item>
    ///   <item>No path match but a <c>content_hash</c> match within the source →
    ///   this is a move: update <c>path</c>, append the old path to
    ///   <c>previous_paths</c>, re-stamp. Result: <see cref="DocumentUpsertKind.Moved"/>.</item>
    ///   <item>No match at all → insert. Result: <see cref="DocumentUpsertKind.Added"/>.</item>
    /// </list>
    /// The caller supplies <paramref name="document"/> pre-populated with the
    /// candidate path, content, hash, and <c>sync_run_id</c>; the store decides
    /// which of the four outcomes applies and returns the persisted row.
    /// </remarks>
    Task<DocsPublicDocumentUpsertResult> UpsertAsync(
        DocsPublicDocument document,
        CancellationToken ct
    );

    /// <summary>Lists every document for one public source, full content included.</summary>
    /// <remarks>
    /// For move-detection/diagnostics only (needs <see cref="DocsPublicDocument.ContentHash"/>
    /// and the full body for logging). Listing documents for display should use
    /// <see cref="ListSummariesBySourceAsync"/> instead, which skips the (often large)
    /// markdown body.
    /// </remarks>
    Task<IReadOnlyList<DocsPublicDocument>> ListBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    );

    /// <summary>
    /// Lists every document for one public source without loading each
    /// document's markdown body — for the document-list endpoint, which only
    /// ever renders <see cref="DocsPublicDocument.Title"/>/<see cref="DocsPublicDocument.Path"/>/etc,
    /// never <see cref="DocsPublicDocument.Content"/>. A shared public source can
    /// be large, so skipping the body avoids materializing every document's
    /// full text into memory just to filter and summarize it.
    /// </summary>
    Task<IReadOnlyList<DocsPublicDocument>> ListSummariesBySourceAsync(
        string publicSourceId,
        CancellationToken ct
    );

    /// <summary>
    /// Gets one document by path within a public source: exact match, then a
    /// suffix/partial-path match (same resolution tier as
    /// <c>ILibraryDocumentStore.GetByPathAsync</c>), then a recorded previous
    /// path (rename alias).
    /// </summary>
    Task<DocsPublicDocument?> GetByPathAsync(
        string publicSourceId,
        string path,
        CancellationToken ct
    );

    /// <summary>
    /// Deletes documents in <paramref name="publicSourceId"/> whose
    /// <c>sync_run_id</c> does not match <paramref name="currentSyncRunId"/> —
    /// the deletion sweep run only after a clean (no per-file failures) pass.
    /// Returns the number of rows deleted.
    /// </summary>
    Task<int> DeleteUnstampedAsync(
        string publicSourceId,
        string currentSyncRunId,
        CancellationToken ct
    );

}

/// <summary>Outcome of one <see cref="IDocsPublicDocumentStore.UpsertAsync"/> call.</summary>
public enum DocumentUpsertKind
{
    /// <summary>No existing row matched; a new document was inserted.</summary>
    Added,

    /// <summary>An existing row at the same path had changed content.</summary>
    Updated,

    /// <summary>An existing row's content_hash was found at a new path.</summary>
    Moved,

    /// <summary>An existing row matched with identical content; only the sync stamp changed.</summary>
    Unchanged,
}

/// <summary>Persisted document plus the upsert outcome that produced it.</summary>
/// <param name="Document">The persisted row.</param>
/// <param name="Kind">Which of the four upsert branches applied.</param>
public sealed record DocsPublicDocumentUpsertResult(
    DocsPublicDocument Document,
    DocumentUpsertKind Kind
);
