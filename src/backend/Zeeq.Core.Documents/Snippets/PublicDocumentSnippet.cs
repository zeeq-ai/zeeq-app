using NpgsqlTypes;
using Pgvector;

namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// An indexed, searchable content snippet derived from a global <see cref="DocsPublicDocument"/>.
/// </summary>
/// <remarks>
/// The public sibling of <see cref="LibraryDocumentSnippet"/>: same shape, but with no
/// org/team/library scoping. Public documents are embedded once and shared across every
/// subscribing org — per-org visibility is resolved at query time by joining through the
/// subscribing library's <c>PublicSourceId</c>. <see cref="PublicSourceId"/> is the scope key
/// (indexed), replacing the private table's org/library leading columns.
///
/// <para>
/// <b>Refactor candidate.</b> This type and <see cref="LibraryDocumentSnippet"/> are close
/// duplicates — every member except the scope key (<see cref="PublicSourceId"/> here vs.
/// org/team/library there) is identical in name and type. Once a read-side consumer (hybrid
/// search results, MCP formatting) exists to pin down which fields it actually needs, extract a
/// shared read interface/DTO rather than guessing the cut now — e.g.
/// <see cref="SearchVector"/>/<see cref="Embedding"/> are storage/ranking internals a
/// display-facing consumer probably shouldn't see.
/// </para>
/// </remarks>
public class PublicDocumentSnippet
{
    /// <summary>
    /// Stable snippet identifier, e.g. <c>snip_0192f0c1e3a97d4e...</c>.
    /// </summary>
    public string Id { get; init; } = null!;

    /// <summary>
    /// Owning public source (scope key; indexed).
    /// </summary>
    public string PublicSourceId { get; init; } = null!;

    /// <summary>
    /// FK to the source <see cref="DocsPublicDocument"/>. The document path/title are resolved at
    /// read time, so a rename never re-embeds this snippet.
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
    /// Identifiers extracted from code content, lowercased for matching. Empty for sections.
    /// </summary>
    public string[] Identifiers { get; set; } = [];

    /// <summary>
    /// The exact (already token-truncated) text that gets embedded — see
    /// <see cref="LibraryDocumentSnippet.EmbeddingPayload"/> for the full rationale.
    /// </summary>
    public string EmbeddingPayload { get; set; } = null!;

    /// <summary>
    /// SHA-256 (hex) of <see cref="EmbeddingPayload"/>. Reconciliation matches on this.
    /// </summary>
    public string ContentHash { get; set; } = null!;

    /// <summary>
    /// Disambiguates snippets with an identical <see cref="ContentHash"/> within one document.
    /// </summary>
    public int Ordinal { get; set; }

    /// <summary>
    /// Estimated token count of the embedding payload (post-truncation).
    /// </summary>
    public int TokenCount { get; set; }

    /// <summary>
    /// Embedding vector (<c>halfvec(768)</c> in storage). Null until embedded.
    /// </summary>
    public HalfVector? Embedding { get; set; }

    /// <summary>
    /// Model+dimension stamp the embedding was produced with, e.g. <c>qwen3-embedding-8b@768</c>.
    /// </summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>
    /// Durable embedding-claim lease timestamp — prevents double-embedding across replicas and
    /// self-heals orphaned claims from a crashed worker.
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
