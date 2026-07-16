using NpgsqlTypes;
using Pgvector;

namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// An indexed, searchable content snippet (a prose section or a code sample) derived from a
/// private <see cref="LibraryDocument"/>.
/// </summary>
/// <remarks>
/// Composed by <see cref="SnippetComposer"/> from a parsed markdown document and reconciled
/// against stored rows by the snippet store's <c>ReplaceForDocumentAsync</c>. Search combines
/// the pgvector HNSW arm (<see cref="Embedding"/>) with the FTS arm (<see cref="SearchVector"/>)
/// via RRF fusion. The <see cref="PublicDocumentSnippet"/> sibling carries the same shape minus
/// the org/team/library scoping (public documents are global — one row serves every subscribing
/// org, with visibility resolved at query time via the library → public-source join).
///
/// Scoping keys (<see cref="OrganizationId"/>, <see cref="LibraryId"/>) lead every index per the
/// distribution-key data-modelling rule. <see cref="DocumentId"/> is the FK back to the document;
/// the document path is resolved at read time so renames never force a re-embed.
///
/// <para>
/// <b>Refactor candidate.</b> This type and <see cref="PublicDocumentSnippet"/> are close
/// duplicates — every member except the scope keys (org/team/library here vs.
/// <see cref="PublicDocumentSnippet.PublicSourceId"/> there) is identical in name and type. Once a
/// read-side consumer (hybrid search results, MCP formatting) exists to pin down which fields it
/// actually needs, extract a shared read interface/DTO rather than guessing the cut now — e.g.
/// <see cref="SearchVector"/>/<see cref="Embedding"/> are storage/ranking internals a
/// display-facing consumer probably shouldn't see.
/// </para>
/// </remarks>
public class LibraryDocumentSnippet
{
    /// <summary>
    /// Stable snippet identifier, e.g. <c>snip_0192f0c1e3a97d4e...</c>.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Organization that owns the snippet (distribution key).
    /// </summary>
    public string OrganizationId { get; init; } = null!;

    /// <summary>
    /// Optional team that owns the snippet within the organization (copied from the document).
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Library that contains the source document.
    /// </summary>
    public string LibraryId { get; init; } = null!;

    /// <summary>
    /// FK to the source <see cref="LibraryDocument"/>. The document path/title are resolved at
    /// read time from the document table, so a rename never re-embeds this snippet.
    /// </summary>
    public string DocumentId { get; init; } = null!;

    /// <summary>
    /// Whether this is a prose <see cref="SnippetKind.Section"/> or a <see cref="SnippetKind.Code"/> sample.
    /// </summary>
    public SnippetKind Kind { get; init; }

    /// <summary>
    /// Heading text that owns this snippet (no <c>#</c> markers).
    /// </summary>
    public string Header { get; set; } = null!;

    /// <summary>
    /// Hierarchical heading path, e.g. <c>"Guide &gt; Install &gt; Linux"</c>.
    /// </summary>
    public string HeadingPath { get; set; } = null!;

    /// <summary>
    /// Fence language for code snippets (e.g. <c>cs</c>), null for sections.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Resolved fence tag for code snippets, null for sections.
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Text between the owning heading and the code fence (code snippets only).
    /// </summary>
    public string? PrecedingText { get; set; }

    /// <summary>
    /// The snippet body: section prose or code content.
    /// </summary>
    public string Content { get; set; } = null!;

    /// <summary>
    /// Identifiers extracted from code content (camelCase/PascalCase/snake_case/dotted), lowercased
    /// for matching. Empty for sections. Powers the exact-identifier boost at search time.
    /// </summary>
    public string[] Identifiers { get; set; } = [];

    /// <summary>
    /// The exact (already token-truncated) text that gets embedded — title + heading path +
    /// fence metadata + preceding text + content, per <see cref="SnippetComposer"/>'s formula.
    /// Persisted so the embedding pipeline can send it to the provider without reconstructing it
    /// from a document join; <see cref="ContentHash"/> is this text's SHA-256 hex digest.
    /// </summary>
    public string EmbeddingPayload { get; set; } = null!;

    /// <summary>
    /// SHA-256 (hex) of <see cref="EmbeddingPayload"/>. Reconciliation matches on this: an
    /// unchanged hash keeps its existing embedding; any payload change (including a
    /// heading-path move) re-embeds.
    /// </summary>
    public string ContentHash { get; set; } = null!;

    /// <summary>
    /// Disambiguates snippets with an identical <see cref="ContentHash"/> within one document
    /// (e.g. two identical code fences), so reconciliation matches them stably.
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Estimated token count of the embedding payload (post-truncation).
    /// </summary>
    public int TokenCount { get; set; }

    /// <summary>
    /// Embedding vector (<c>halfvec(768)</c> in storage). Null until embedded — the FTS arm is
    /// fully functional in the meantime.
    /// </summary>
    public HalfVector? Embedding { get; set; }

    /// <summary>
    /// Model+dimension stamp the embedding was produced with, e.g. <c>qwen3-embedding-8b@768</c>.
    /// A model or dimension change makes the row claimable for re-embedding.
    /// </summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// Durable embedding-claim lease timestamp. A transaction-scoped row lock releases long before
    /// the async provider call completes, so this column (not a lock) prevents double-embedding
    /// across replicas; an expired lease self-heals an orphaned claim from a crashed worker.
    /// </summary>
    public DateTimeOffset? EmbeddingStartedAt { get; set; }

    /// <summary>
    /// DB-generated weighted FTS vector maintained by Postgres on every write.
    /// </summary>
    public NpgsqlTsVector SearchVector { get; private set; } = null!;

    /// <summary>
    /// Timestamp when the snippet row was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Timestamp when the snippet row was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
