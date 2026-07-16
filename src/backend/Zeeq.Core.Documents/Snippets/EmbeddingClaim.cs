namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// One snippet claimed for embedding by <see cref="ISnippetStore{TDocument}.ClaimMissingEmbeddingsAsync"/>.
/// </summary>
/// <remarks>
/// Deliberately narrow — the embedding pipeline only needs the snippet id (to write the result
/// back) and the exact payload text to send to the provider. It never needs the full snippet
/// entity or the owning document type, so this is a table-neutral shape shared by both stores.
/// </remarks>
/// <param name="Id">Stable snippet identifier.</param>
/// <param name="EmbeddingPayload">The exact (already token-truncated) text to embed.</param>
public sealed record EmbeddingClaim(string Id, string EmbeddingPayload);
