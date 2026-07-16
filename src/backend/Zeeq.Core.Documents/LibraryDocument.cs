using NpgsqlTypes;

namespace Zeeq.Core.Documents;

/// <summary>
/// A markdown document stored within a library.
/// </summary>
/// <remarks>
/// Path is the primary identity key within an organization and library. All search-facing
/// columns are derived by the markdown parser and normalized by the write path.
/// </remarks>
public class LibraryDocument
{
    /// <summary>
    /// Stable document identifier.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Organization that owns the document.
    /// </summary>
    public string OrganizationId { get; init; } = null!;

    /// <summary>
    /// Optional team that owns the document within the organization.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Library that contains the document.
    /// </summary>
    public string LibraryId { get; init; } = null!;

    /// <summary>
    /// Normalized full path, unique per organization and library.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="RenameTo"/> over setting this directly during a rename — it keeps
    /// <see cref="PreviousPaths"/> and <see cref="UpdatedAt"/> consistent in one call.
    /// Must remain <c>set</c> (not <c>init</c>) for object-initializer construction and EF Core
    /// insert tracking. <c>path_reversed</c> is a stored computed column and regenerates on UPDATE.
    /// </remarks>
    public string Path { get; set; } = null!;

    /// <summary>
    /// DB-generated reversed path used by the suffix/partial-path B-tree index.
    /// </summary>
    public string PathReversed { get; private set; } = null!;

    /// <summary>
    /// Prior paths this document has lived at, so old links/references still resolve after a rename.
    /// Appended on every move; never removed. GIN-indexed for membership lookup (D-4).
    /// </summary>
    public string[] PreviousPaths { get; set; } = [];

    /// <summary>
    /// Display title as authored or resolved by the parser.
    /// </summary>
    public string Title { get; set; } = null!;

    /// <summary>
    /// Normalized title used for fuzzy matching.
    /// </summary>
    public string TitleNormalized { get; set; } = null!;

    /// <summary>
    /// Normalized keywords derived from front matter.
    /// </summary>
    public string[] Keywords { get; set; } = [];

    /// <summary>
    /// Plain heading text as authored, in document order.
    /// </summary>
    public string[] Headings { get; set; } = [];

    /// <summary>
    /// Full markdown source, including front matter when present.
    /// </summary>
    public string Content { get; set; } = null!;

    /// <summary>
    /// DB-generated weighted search vector maintained by Postgres on every write.
    /// </summary>
    public NpgsqlTsVector SearchVector { get; private set; } = null!;

    /// <summary>
    /// Secondary processing state for indexing and embedding workflows.
    /// </summary>
    public DocumentProcessingStatus ProcessingStatus { get; set; } =
        DocumentProcessingStatus.Pending;

    /// <summary>
    /// Estimated token count for the searchable content.
    /// </summary>
    public int TokenCount { get; set; }

    /// <summary>
    /// SHA-256 hex hash of the searchable content.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// Optional external source metadata for future ingestion flows.
    /// </summary>
    public LibraryDocumentSourceOrigin? SourceOrigin { get; init; }

    /// <summary>
    /// UUIDv7 stamp of the ingest run that last touched this document. Used by the
    /// deletion sweep: after a clean pass, documents whose <c>sync_run_id</c> does not
    /// match the current run are deleted (they are absent upstream). Null for
    /// hand-authored documents.
    /// </summary>
    public string? SyncRunId { get; set; }

    /// <summary>
    /// When true, this document is operational/informational content that code-review
    /// agents must not consult: it is hidden from list and search results on the
    /// code-review execution path.
    /// </summary>
    /// <remarks>
    /// The filter applies only when <see cref="DocumentSearchScope.ForCodeReviewExecution"/>
    /// is marked (the reviewer tool call path in <c>CodeReviewAgentExecutor.BuildLibraryTools</c>);
    /// the interactive MCP server and HTTP endpoints see excluded documents normally, and
    /// direct path resolution (<c>read_document_by_path</c>) always succeeds. V1 scope is
    /// hand-authored documents only — the API rejects toggling synced documents
    /// (<see cref="SyncRunId"/> not null) because an ingest run owns their lifecycle.
    /// </remarks>
    public bool ExcludedFromCodeReviews { get; set; }

    /// <summary>
    /// Timestamp when the document was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the document was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Moves the document to <paramref name="newPath"/>, recording the current path as an alias
    /// and refreshing <see cref="UpdatedAt"/>. No-ops when <paramref name="newPath"/> equals
    /// the current <see cref="Path"/>.
    /// </summary>
    /// <param name="newPath">The normalized target path.</param>
    public void RenameTo(string newPath)
    {
        // NOTE: empty/whitespace validation is intentionally left to the caller; all production
        // call sites run newPath through DocumentNormalizer.NormalizePath first, which throws.
        if (Path == newPath)
            return;

        // If the target was a previous alias of this document, remove it first so the live
        // path does not end up duplicated in PreviousPaths after the move.
        PreviousPaths = [.. PreviousPaths.Where(p => p != newPath), Path];
        Path = newPath;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
