using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Documents.Parsing;

namespace Zeeq.Core.Documents.Snippets;

/// <summary>
/// Composes a parsed markdown document into a flat list of searchable snippets (prose sections
/// and code samples).
/// </summary>
/// <remarks>
/// The write-path complement to <see cref="MarkdownParser"/>: it turns a
/// <see cref="ParsedMarkdown"/> into <see cref="ComposedSnippet"/> rows the sweep persists.
/// Composition is pure — no DB, no scoping keys, no embeddings — so the sweep can run it on a
/// CPU-bound stage and unit tests can assert payload/hash behavior with zero infrastructure.
///
/// Flow (spec Phase 1): each <see cref="ParsedMarkdown.Sections"/> entry with a body of at least
/// <see cref="SnippetIndexingSettings.MinSectionChars"/> becomes one section snippet; each
/// <see cref="ParsedMarkdown.Snippets"/> entry becomes one code snippet with extracted identifiers.
/// Embedding payloads carry synthetic context (document title + heading path + fence metadata) so
/// the vector arm has enough signal; the payload is token-truncated <em>before</em> hashing so the
/// stored <c>ContentHash</c> is stable across re-runs. Duplicate payloads within a document get a
/// distinct <see cref="ComposedSnippet.Ordinal"/>; the document is capped at
/// <see cref="SnippetIndexingSettings.MaxSnippetsPerDocument"/>.
/// </remarks>
public static class SnippetComposer
{
    /// <summary>
    /// Composes snippets for <paramref name="parsed"/> using <paramref name="settings"/>.
    /// </summary>
    /// <param name="parsed">The parsed markdown document.</param>
    /// <param name="settings">Indexing tunables (min section size, payload token cap, per-doc cap).</param>
    /// <returns>
    /// The composed snippets in document order (sections first, then code), capped at
    /// <see cref="SnippetIndexingSettings.MaxSnippetsPerDocument"/>. Ordinals disambiguate
    /// identical payloads within the returned set.
    /// </returns>
    public static IReadOnlyList<ComposedSnippet> Compose(
        ParsedMarkdown parsed,
        SnippetIndexingSettings settings
    )
    {
        var documentTitle = parsed.Title;

        // Tracks how many times a given content hash has appeared so far, so identical payloads
        // (e.g. two identical code fences) receive distinct, stable ordinals.
        var ordinalByHash = new Dictionary<string, int>(StringComparer.Ordinal);
        var composed = new List<ComposedSnippet>();

        foreach (var section in parsed.Sections)
        {
            if (composed.Count >= settings.MaxSnippetsPerDocument)
            {
                break;
            }

            if (section.Body.Length < settings.MinSectionChars)
            {
                continue;
            }

            // Section payload: title + heading path + body. Excludes fenced code by design — the
            // parser strips code from section bodies, and code is covered by code snippets.
            var payload = TruncatePayload(
                $"{documentTitle}\n{section.HeadingPath}\n{section.Body}",
                settings.MaxPayloadTokens
            );

            composed.Add(
                BuildSnippet(
                    kind: SnippetKind.Section,
                    header: section.Header,
                    headingPath: section.HeadingPath,
                    language: null,
                    tag: null,
                    precedingText: null,
                    content: section.Body,
                    identifiers: [],
                    payload: payload,
                    ordinalByHash: ordinalByHash
                )
            );
        }

        foreach (var snippet in parsed.Snippets)
        {
            if (composed.Count >= settings.MaxSnippetsPerDocument)
            {
                break;
            }

            var language = string.IsNullOrEmpty(snippet.Language) ? null : snippet.Language;
            var tag = string.IsNullOrEmpty(snippet.Tag) ? null : snippet.Tag;
            var preceding = string.IsNullOrEmpty(snippet.Preceding) ? null : snippet.Preceding;

            // Code payload: title + heading path + (language) tag + preceding + content. Fence
            // metadata is part of the payload (and thus the hash) so a language/tag change re-embeds.
            var payload = TruncatePayload(
                $"{documentTitle}\n{snippet.HeadingPath} ({snippet.Language}) {snippet.Tag}\n{snippet.Preceding}\n{snippet.Content}",
                settings.MaxPayloadTokens
            );

            composed.Add(
                BuildSnippet(
                    kind: SnippetKind.Code,
                    header: snippet.Header,
                    headingPath: snippet.HeadingPath,
                    language: language,
                    tag: tag,
                    precedingText: preceding,
                    content: snippet.Content,
                    identifiers: SnippetIdentifierExtractor.Extract(
                        snippet.Content,
                        SnippetIdentifierExtractor.IndexMinLength
                    ),
                    payload: payload,
                    ordinalByHash: ordinalByHash
                )
            );
        }

        return composed;
    }

    /// <summary>
    /// Builds one composed snippet: hashes the (already truncated) payload, counts its tokens, and
    /// assigns the next ordinal for that hash.
    /// </summary>
    private static ComposedSnippet BuildSnippet(
        SnippetKind kind,
        string header,
        string headingPath,
        string? language,
        string? tag,
        string? precedingText,
        string content,
        string[] identifiers,
        string payload,
        Dictionary<string, int> ordinalByHash
    )
    {
        var hash = ComputeSha256Hex(payload);
        var ordinal = ordinalByHash.TryGetValue(hash, out var seen) ? seen : 0;
        ordinalByHash[hash] = ordinal + 1;

        return new ComposedSnippet(
            Kind: kind,
            Header: header,
            HeadingPath: headingPath,
            Language: language,
            Tag: tag,
            PrecedingText: precedingText,
            Content: content,
            Identifiers: identifiers,
            EmbeddingPayload: payload,
            ContentHash: hash,
            Ordinal: ordinal,
            TokenCount: TiktokenCounter.CountTokens(payload)
        );
    }

    /// <summary>
    /// Truncates the embedding payload to the token ceiling. Truncation happens before hashing so
    /// the stored hash is over the exact bytes that get embedded.
    /// </summary>
    private static string TruncatePayload(string payload, int maxTokens) =>
        TiktokenCounter.Truncate(payload, maxTokens);

    /// <summary>
    /// Computes the lowercase SHA-256 hex digest of <paramref name="content"/> (mirrors the
    /// document write path's hashing so the two are consistent).
    /// </summary>
    private static string ComputeSha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
