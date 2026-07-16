using NpgsqlTypes;

namespace Zeeq.Core.Documents;

/// <summary>
/// A markdown document ingested from a public repository, shared globally across all
/// subscribing organizations.
/// </summary>
/// <remarks>
/// There is no organization column — one row serves all orgs that subscribe to the
/// parent <see cref="DocsPublicSource"/>. Per-org visibility is enforced at query time
/// via the library's include/exclude filter. Move detection uses <c>content_hash</c>
/// indexed within the source scope.
/// </remarks>
public class DocsPublicDocument
{
    /// <summary>
    /// Stable document identifier.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Owning public source.
    /// </summary>
    public string PublicSourceId { get; init; } = null!;

    /// <summary>
    /// Normalized full path relative to the repository root, unique per source.
    /// </summary>
    public string Path { get; set; } = null!;

    /// <summary>
    /// DB-generated reversed path used by the suffix/partial-path B-tree index.
    /// </summary>
    public string PathReversed { get; private set; } = null!;

    /// <summary>
    /// Prior paths this document has lived at, so old links still resolve after a rename.
    /// Append-only; never removed. GIN-indexed.
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
    /// SHA-256 hex hash of the searchable content, used for change detection and move dedup.
    /// </summary>
    public string ContentHash { get; set; } = null!;

    /// <summary>
    /// Estimated token count for the searchable content.
    /// </summary>
    public int TokenCount { get; set; }

    /// <summary>
    /// Secondary processing state for indexing and embedding workflows.
    /// </summary>
    public DocumentProcessingStatus ProcessingStatus { get; set; } =
        DocumentProcessingStatus.Pending;

    /// <summary>
    /// UUIDv7 stamp of the ingest run that last touched this document. Used by the
    /// deletion sweep: after a clean pass, documents whose <c>sync_run_id</c> does not
    /// match the current run are deleted (they are absent upstream).
    /// </summary>
    public string? SyncRunId { get; set; }

    /// <summary>
    /// Timestamp when the document was first ingested.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the document was last updated (content or path change).
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
