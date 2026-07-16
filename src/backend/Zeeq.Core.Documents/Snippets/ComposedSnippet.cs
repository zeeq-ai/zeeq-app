namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// A provider- and table-neutral composed snippet produced by <see cref="SnippetComposer"/>.
/// </summary>
/// <remarks>
/// The composer runs purely over a parsed markdown document (no scoping keys, no DB, no vectors),
/// so its output is a plain value that the sweep maps onto either a
/// <see cref="LibraryDocumentSnippet"/> or a <see cref="PublicDocumentSnippet"/> by adding the
/// scope keys and identity. Keeping composition side-effect free makes it trivially unit-testable.
///
/// <see cref="EmbeddingPayload"/> is the exact (already token-truncated) text that gets embedded
/// and hashed; <see cref="ContentHash"/> is the SHA-256 hex of that payload. Reconciliation matches
/// stored rows by <c>(Kind, ContentHash, Ordinal)</c>, so a payload change (including a heading-path
/// move) produces a new hash and forces a re-embed.
/// </remarks>
/// <param name="Kind">Section or code.</param>
/// <param name="Header">Owning heading text.</param>
/// <param name="HeadingPath">Hierarchical heading path, e.g. <c>"Guide &gt; Install"</c>.</param>
/// <param name="Language">Fence language (code only), else null.</param>
/// <param name="Tag">Resolved fence tag (code only), else null.</param>
/// <param name="PrecedingText">Text before the fence (code only), else null.</param>
/// <param name="Content">Section prose or code content.</param>
/// <param name="Identifiers">Lowercased extracted identifiers (code only), else empty.</param>
/// <param name="EmbeddingPayload">The truncated text embedded and hashed.</param>
/// <param name="ContentHash">SHA-256 hex of <paramref name="EmbeddingPayload"/>.</param>
/// <param name="Ordinal">Disambiguates duplicate hashes within one document.</param>
/// <param name="TokenCount">Token count of <paramref name="EmbeddingPayload"/>.</param>
public sealed record ComposedSnippet(
    SnippetKind Kind,
    string Header,
    string HeadingPath,
    string? Language,
    string? Tag,
    string? PrecedingText,
    string Content,
    string[] Identifiers,
    string EmbeddingPayload,
    string ContentHash,
    int Ordinal,
    int TokenCount
);
