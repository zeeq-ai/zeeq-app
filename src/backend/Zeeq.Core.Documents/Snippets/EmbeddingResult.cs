using Pgvector;

namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// A produced embedding vector to write back for one claimed snippet.
/// </summary>
/// <remarks>
/// <see cref="HalfVector"/>, not <see cref="Vector"/> — the storage column is
/// <c>halfvec(768)</c>, and Npgsql's Pgvector integration requires the CLR type to match the
/// Postgres type exactly for reads (a <see cref="Vector"/>-typed read against a <c>halfvec</c>
/// column throws <see cref="InvalidCastException"/>; verified empirically 2026-07-11).
/// </remarks>
/// <param name="Id">Stable snippet identifier (matches an <see cref="EmbeddingClaim.Id"/>).</param>
/// <param name="Embedding">The generated vector.</param>
public sealed record EmbeddingResult(string Id, HalfVector Embedding);
