using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Parsing;
using Zeeq.Core.Documents.Snippets;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Maps document-library domain entities to their API response contracts.
/// </summary>
public static class LibraryEndpointMapping
{
    /// <summary>
    /// Derives the human-readable origin string from the document's source metadata.
    /// A null <see cref="LibraryDocument.SourceOrigin"/> means the document is owned locally.
    /// </summary>
    /// <param name="document">The document to inspect.</param>
    /// <returns><c>"local"</c> when owned by Zeeq; otherwise <c>"remote"</c>.</returns>
    private static string OriginOf(LibraryDocument document) =>
        document.SourceOrigin is null ? "local" : "remote";

    /// <summary>
    /// Maps a library entity to its API response.
    /// </summary>
    /// <param name="library">The library to map.</param>
    /// <param name="publicSourcesById">
    /// Public sources keyed by id, batch-fetched by the caller for every
    /// distinct <see cref="Library.PublicSourceId"/> in the set being mapped
    /// (one query, not one per library — see
    /// <see cref="IDocsPublicSourceStore.GetByIdsAsync"/>). Required even for
    /// a single local/private library so a caller can't accidentally omit it
    /// for a public-source one; pass an empty dictionary when none are needed.
    /// </param>
    /// <returns>The response contract describing the library.</returns>
    public static LibraryResponse ToResponse(
        Library library,
        IReadOnlyDictionary<string, DocsPublicSource> publicSourcesById
    ) =>
        new(
            library.Id,
            library.Name,
            library.Description,
            ToSourceResponse(library, publicSourcesById),
            library.CreatedAt,
            library.UpdatedAt
        );

    private static LibrarySourceResponse? ToSourceResponse(
        Library library,
        IReadOnlyDictionary<string, DocsPublicSource> publicSourcesById
    )
    {
        if (library.PublicSourceId is { } publicSourceId)
        {
            // NOTE: (reviewed and kept as-is) a missing lookup entry (caller
            // forgot to batch-fetch it, or the source was deleted concurrently)
            // degrades to "no source info" rather than throwing — the library
            // itself is still valid to return, and every caller of this method
            // constructs publicSourcesById via LoadPublicSourceAsync/GetByIdsAsync
            // scoped to exactly the libraries being mapped, so a miss here would
            // only ever be that rare concurrent-delete race, not a caller bug.
            if (!publicSourcesById.TryGetValue(publicSourceId, out var source))
            {
                return null;
            }

            return new LibrarySourceResponse(
                Kind: "Public",
                RepoUrl: source.RepoUrl,
                SyncStatus: source.SyncStatus,
                NextSyncAt: source.NextSyncAt,
                LastSyncedAt: source.SyncedAt,
                Quarantined: source.Status == "quarantined",
                IncludeFilters: library.IncludeFilters,
                ExcludeFilters: library.ExcludeFilters
            );
        }

        if (library.SourceKind is not null && library.SourceRepoUrl is not null)
        {
            return new LibrarySourceResponse(
                Kind: "Private",
                RepoUrl: library.SourceRepoUrl,
                SyncStatus: library.SyncStatus,
                NextSyncAt: library.NextSyncAt,
                LastSyncedAt: library.SourceSyncedAt,
                Quarantined: false,
                IncludeFilters: library.IncludeFilters,
                ExcludeFilters: library.ExcludeFilters
            );
        }

        return null;
    }

    /// <summary>Empty-dictionary convenience for mapping a library with no repository source.</summary>
    public static readonly IReadOnlyDictionary<string, DocsPublicSource> NoPublicSources =
        new Dictionary<string, DocsPublicSource>();

    /// <summary>
    /// Convenience for mapping a single library (not a list): resolves its
    /// public source, if any, into the one-entry dictionary
    /// <see cref="ToResponse(Library,IReadOnlyDictionary{string,DocsPublicSource})"/>
    /// expects.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, DocsPublicSource>> LoadPublicSourceAsync(
        IDocsPublicSourceStore publicSources,
        Library library,
        CancellationToken ct
    )
    {
        if (library.PublicSourceId is not { } publicSourceId)
        {
            return NoPublicSources;
        }

        var source = await publicSources.GetByIdAsync(publicSourceId, ct);
        return source is null
            ? NoPublicSources
            : new Dictionary<string, DocsPublicSource> { [source.Id] = source };
    }

    /// <summary>
    /// Maps a document entity to its API summary response (no body).
    /// </summary>
    /// <remarks>
    /// The response is a summary that omits the document body; the processing status is rendered as
    /// its string name for stable, human-readable output. Origin is derived from
    /// <see cref="OriginOf"/> so the front-end can gate editing of ingested documents.
    /// </remarks>
    /// <param name="document">The document to map.</param>
    /// <returns>The response contract describing the document.</returns>
    public static DocumentResponse ToResponse(LibraryDocument document) =>
        new(
            document.Id,
            document.Path,
            document.Title,
            document.Keywords,   // NOTE: non-nullable; LibraryDocument initializes both to []
            document.Headings,
            document.TokenCount,
            document.ProcessingStatus.ToString(),
            OriginOf(document),
            document.ExcludedFromCodeReviews,
            document.CreatedAt,
            document.UpdatedAt
        );

    /// <summary>
    /// Maps a document entity to a full-content response including the markdown body.
    /// </summary>
    /// <param name="document">The document to map.</param>
    /// <returns>The full-content response contract.</returns>
    public static DocumentContentResponse ToContentResponse(LibraryDocument document) =>
        new(
            document.Id,
            document.Path,
            document.Title,
            document.Keywords,
            document.Headings,
            document.TokenCount,
            document.ProcessingStatus.ToString(),
            OriginOf(document),
            document.ExcludedFromCodeReviews,
            document.Content,
            document.CreatedAt,
            document.UpdatedAt
        );

    /// <summary>
    /// Maps a public document entity to its API summary response (no body).
    /// Always <c>"remote"</c> origin — a public document is never hand-authored.
    /// </summary>
    public static DocumentResponse ToResponse(DocsPublicDocument document) =>
        new(
            document.Id,
            document.Path,
            document.Title,
            document.Keywords,
            document.Headings,
            document.TokenCount,
            document.ProcessingStatus.ToString(),
            "remote",
            // Public documents cannot be review-excluded in v1 — the flag lives on the per-org
            // library document row, and shared public rows have no per-org override.
            ExcludedFromCodeReviews: false,
            document.CreatedAt,
            document.UpdatedAt
        );

    /// <summary>Maps a public document entity to a full-content response including the markdown body.</summary>
    public static DocumentContentResponse ToContentResponse(DocsPublicDocument document) =>
        new(
            document.Id,
            document.Path,
            document.Title,
            document.Keywords,
            document.Headings,
            document.TokenCount,
            document.ProcessingStatus.ToString(),
            "remote",
            ExcludedFromCodeReviews: false,
            document.Content,
            document.CreatedAt,
            document.UpdatedAt
        );

    /// <summary>
    /// Resolves a public-source library's effective include/exclude filter —
    /// the library's own override when non-empty, else the source's default —
    /// matching the same rule <c>PublicRepositorySyncRequestedHandler</c> uses
    /// to decide what to ingest. Document-read endpoints re-apply this at
    /// query time (spec §14.4: "per-org visibility is enforced at query time
    /// via the library's include/exclude filter") since <c>docs_public_documents</c>
    /// holds the *union* of every subscriber's scope, not just this one org's.
    /// </summary>
    public static Zeeq.Core.Documents.Dispatch.EffectiveFilter ResolveEffectiveFilter(
        Library library,
        DocsPublicSource source
    ) =>
        new(
            library.IncludeFilters.Length > 0 ? library.IncludeFilters : source.DefaultIncludeFilters,
            library.ExcludeFilters.Length > 0 ? library.ExcludeFilters : source.DefaultExcludeFilters
        );

    /// <summary>
    /// Maps a search match to its API response, echoing the library name (D-1).
    /// </summary>
    /// <param name="match">The search hit from the store.</param>
    /// <param name="libraryName">The resolved library name to echo on the response.</param>
    /// <returns>The search-result response contract.</returns>
    public static DocumentSearchResultResponse ToSearchResult(
        LibraryDocumentMatch match,
        string libraryName
    ) =>
        new(
            libraryName,
            match.Document.Path,
            match.Document.Title,
            match.Document.Keywords,
            match.MatchType.ToString(),
            match.FullTextScore,
            match.FuzzyScore
        );

    /// <summary>
    /// Maps a parsed document and its composed snippets to the parse-preview response.
    /// </summary>
    /// <param name="path">The document's normalized path, echoed from the request.</param>
    /// <param name="parsed">The <see cref="MarkdownParser.Parse"/> result.</param>
    /// <param name="snippets">The <see cref="SnippetComposer.Compose"/> result for <paramref name="parsed"/>.</param>
    public static DocumentParsePreviewResponse ToParsePreviewResponse(
        string path,
        ParsedMarkdown parsed,
        IReadOnlyList<ComposedSnippet> snippets
    ) =>
        new(
            path,
            parsed.Title,
            [.. parsed.Keywords],
            [.. parsed.Headings],
            [.. snippets.Select(ToParsePreviewSnippet)]
        );

    private static DocumentParsePreviewSnippetResponse ToParsePreviewSnippet(
        ComposedSnippet snippet
    ) =>
        new(
            snippet.Kind == SnippetKind.Section ? "section" : "code",
            snippet.Header,
            snippet.HeadingPath,
            snippet.Language,
            snippet.Tag,
            snippet.Content,
            snippet.Identifiers,
            snippet.TokenCount
        );
}
