using System.ComponentModel.DataAnnotations;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Request body for creating a document library.
/// </summary>
public sealed record CreateLibraryRequest
{
    /// <summary>
    /// Human-readable library name, unique in the active organization.
    /// </summary>
    [Required, MaxLength(200)]
    public required string Name { get; init; }

    /// <summary>
    /// Optional library description.
    /// </summary>
    [MaxLength(2000)]
    public string? Description { get; init; }

    /// <summary>
    /// Present only when creating a repository-sourced library. Absent means
    /// a plain hand-authored ("local") library, as before this field existed.
    /// </summary>
    public CreateLibrarySourceRequest? Source { get; init; }
}

/// <summary>
/// Source configuration for creating a repository-backed library.
/// </summary>
public sealed record CreateLibrarySourceRequest
{
    /// <summary>Which kind of repository source to create.</summary>
    public required LibrarySourceKindRequest Kind { get; init; }

    /// <summary>
    /// Raw GitHub clone URL. Required and only used when
    /// <see cref="Kind"/> is <see cref="LibrarySourceKindRequest.Public"/>.
    /// </summary>
    [MaxLength(2048)]
    public string? RepoUrl { get; init; }

    /// <summary>
    /// Id of one of the organization's configured GitHub repositories
    /// (<c>ICodeRepositoryStore</c>). Required and only used when
    /// <see cref="Kind"/> is <see cref="LibrarySourceKindRequest.Private"/>.
    /// </summary>
    [MaxLength(128)]
    public string? RepositoryId { get; init; }

    /// <summary>Org-level include path globs. Empty means include everything.</summary>
    public string[] IncludeFilters { get; init; } = [];

    /// <summary>Org-level exclude path globs. Empty means exclude nothing.</summary>
    public string[] ExcludeFilters { get; init; } = [];
}

/// <summary>Discriminates which kind of repository source a library create request configures.</summary>
public enum LibrarySourceKindRequest
{
    /// <summary>A public, globally-shared repository (subscribes to/creates a <c>docs_public_sources</c> row).</summary>
    Public,

    /// <summary>A private, organization-owned repository ingested only for this library.</summary>
    Private,
}

/// <summary>
/// Request body for updating a document library.
/// </summary>
public sealed record UpdateLibraryRequest
{
    /// <summary>
    /// New human-readable library name.
    /// </summary>
    [Required, MaxLength(200)]
    public required string Name { get; init; }

    /// <summary>
    /// Optional library description.
    /// </summary>
    [MaxLength(2000)]
    public string? Description { get; init; }

    /// <summary>
    /// Org-level include path globs. <see langword="null"/> leaves the
    /// existing value unchanged (e.g. when editing a plain local library);
    /// an empty array explicitly clears it. Ignored for a library with no
    /// repository source. Never changes the repository URL/kind — delete
    /// and recreate the library to change the source.
    /// </summary>
    public string[]? IncludeFilters { get; init; }

    /// <summary>Org-level exclude path globs. Same null-vs-empty semantics as <see cref="IncludeFilters"/>.</summary>
    public string[]? ExcludeFilters { get; init; }
}

/// <summary>
/// Request body for writing a markdown document into a library.
/// </summary>
public sealed record UpsertDocumentRequest
{
    /// <summary>
    /// Caller-supplied document path before normalization.
    /// </summary>
    [MaxLength(200, ErrorMessage = "Document path must be 200 characters or less.")]
    public required string Path { get; init; }

    /// <summary>
    /// Full markdown source to parse and persist.
    /// </summary>
    [MaxLength(20_000, ErrorMessage = "Document content must be 20,000 characters or less.")]
    public required string Content { get; init; }
}

/// <summary>
/// Multipart form request for importing a signed Zeeq library export package.
/// </summary>
/// <remarks>
/// Endpoint-level annotations keep OpenAPI and model binding explicit. The import reader still
/// repeats extension, size, signature, and package validation because client-provided file names
/// and multipart metadata are not a trust boundary.
/// </remarks>
public sealed record LibraryImportUploadRequest
{
    /// <summary>
    /// Signed Zeeq export package. Plain zip files are intentionally not importable.
    /// </summary>
    [Required]
    [FileExtensions(Extensions = "zeeq-export")]
    public IFormFile? File { get; init; }
}

/// <summary>
/// API response for a document library.
/// </summary>
/// <param name="Id">Stable library identifier.</param>
/// <param name="Name">Human-readable library name.</param>
/// <param name="Description">Optional library description.</param>
/// <param name="Source">Present only for a repository-sourced library.</param>
/// <param name="CreatedAt">Timestamp when the library was created.</param>
/// <param name="UpdatedAt">Timestamp when the library was last updated.</param>
public sealed record LibraryResponse(
    string Id,
    string Name,
    string? Description,
    LibrarySourceResponse? Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

/// <summary>
/// Repository-source metadata and sync status for a repository-backed library.
/// </summary>
/// <param name="Kind">"Public" or "Private".</param>
/// <param name="RepoUrl">
/// The resolved clone URL either way — from the joined <c>DocsPublicSource</c>
/// for a public-source library, or the library's own <c>SourceRepoUrl</c> for
/// a private-source library. Never editable after creation.
/// </param>
/// <param name="SyncStatus">idle | queued | running | paused.</param>
/// <param name="NextSyncAt">Next scheduled sync time, if any.</param>
/// <param name="LastSyncedAt">Last successful sync time, if any.</param>
/// <param name="Quarantined">
/// Always <see langword="false"/> for Private. For Public, reflects the
/// joined <c>DocsPublicSource.Status == "quarantined"</c> — the upstream
/// repository is no longer public; documents are frozen, not deleted.
/// </param>
/// <param name="IncludeFilters">Org-level include path globs.</param>
/// <param name="ExcludeFilters">Org-level exclude path globs.</param>
public sealed record LibrarySourceResponse(
    string Kind,
    string RepoUrl,
    string? SyncStatus,
    DateTimeOffset? NextSyncAt,
    DateTimeOffset? LastSyncedAt,
    bool Quarantined,
    string[] IncludeFilters,
    string[] ExcludeFilters
);

/// <summary>
/// API response for a library document.
/// </summary>
/// <param name="Id">Stable document identifier.</param>
/// <param name="Path">Normalized document path.</param>
/// <param name="Title">Display title resolved from the markdown source.</param>
/// <param name="Keywords">Normalized keywords derived from front matter.</param>
/// <param name="Headings">Plain heading text as authored.</param>
/// <param name="TokenCount">Estimated token count for the searchable content.</param>
/// <param name="ProcessingStatus">Secondary indexing state.</param>
/// <param name="Origin">Document source origin: "local" (owned) or "remote" (ingested).</param>
/// <param name="ExcludedFromCodeReviews">
/// True when the document is hidden from list/search results on the code-review execution path
/// (operational/informational content reviewers must not consult). Always false for remote
/// documents — v1 scopes exclusion to hand-authored documents.
/// </param>
/// <param name="CreatedAt">Timestamp when the document was created.</param>
/// <param name="UpdatedAt">Timestamp when the document was last updated.</param>
public sealed record DocumentResponse(
    string Id,
    string Path,
    string Title,
    string[] Keywords,
    string[] Headings,
    int TokenCount,
    string ProcessingStatus,
    string Origin,
    bool ExcludedFromCodeReviews,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

/// <summary>
/// Full document including markdown body, for the editor load path.
/// </summary>
/// <param name="Id">Stable document identifier.</param>
/// <param name="Path">Normalized document path.</param>
/// <param name="Title">Display title resolved from the markdown source.</param>
/// <param name="Keywords">Normalized keywords derived from front matter.</param>
/// <param name="Headings">Plain heading text as authored.</param>
/// <param name="TokenCount">Estimated token count for the searchable content.</param>
/// <param name="ProcessingStatus">Secondary indexing state.</param>
/// <param name="Origin">Document source origin: "local" (owned) or "remote" (ingested).</param>
/// <param name="ExcludedFromCodeReviews">
/// True when the document is hidden from list/search results on the code-review execution path.
/// Drives the editor's exclusion toggle state; always false for remote documents (v1).
/// </param>
/// <param name="Content">Full markdown body.</param>
/// <param name="CreatedAt">Timestamp when the document was created.</param>
/// <param name="UpdatedAt">Timestamp when the document was last updated.</param>
public sealed record DocumentContentResponse(
    string Id,
    string Path,
    string Title,
    string[] Keywords,
    string[] Headings,
    int TokenCount,
    string ProcessingStatus,
    string Origin,
    bool ExcludedFromCodeReviews,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

/// <summary>
/// A ranked search hit for the "Test" panel. <c>Library</c> is echoed from the API so each
/// row carries its owning library name independently (D-1).
/// </summary>
/// <param name="Library">The library name this document belongs to.</param>
/// <param name="Path">Normalized document path.</param>
/// <param name="Title">Display title resolved from the markdown source.</param>
/// <param name="Keywords">Normalized keywords derived from front matter.</param>
/// <param name="MatchType">Which retrieval signal(s) matched.</param>
/// <param name="FullTextScore">Normalized full-text rank in [0,1).</param>
/// <param name="FuzzyScore">Trigram title similarity in [0,1].</param>
public sealed record DocumentSearchResultResponse(
    string Library,
    string Path,
    string Title,
    string[] Keywords,
    string MatchType,
    double FullTextScore,
    double FuzzyScore
);

/// <summary>
/// Request body to rename (move) a document to a new path within the same library (D-3).
/// </summary>
/// <param name="FromPath">Current document path to move from.</param>
/// <param name="ToPath">Target document path to move to.</param>
public sealed record RenameDocumentRequest(
    [property: Required, MaxLength(200)] string FromPath,
    [property: Required, MaxLength(200)] string ToPath
);

/// <summary>
/// Request body to set or clear a document's code-review exclusion flag.
/// </summary>
/// <remarks>
/// Only hand-authored documents can be excluded — the handler rejects synced/remote documents
/// (a sync run owns their lifecycle and would silently fight the flag). The toggle is
/// reversible, so no confirmation flow is required client-side.
/// </remarks>
/// <param name="DocumentId">Stable document identifier for the loaded document to update.</param>
/// <param name="Excluded">True to hide the document from code-review list/search results.</param>
public sealed record SetDocumentReviewExclusionRequest(
    [property: Required, MaxLength(128)] string DocumentId,
    bool Excluded
);

/// <summary>
/// Request body for updating the set of repositories mapped to a library.
/// Replaces the full set of repository mappings for this library.
/// </summary>
public sealed record UpdateLibraryRepositoryMappingsRequest
{
    /// <summary>
    /// IDs of the repositories that should be mapped to this library.
    /// Must be non-deleted repository IDs belonging to the caller's organization.
    /// An empty array clears all mappings.
    /// </summary>
    [Required]
    public required string[] RepositoryIds { get; init; }
}

/// <summary>
/// Response body for the library repository mappings endpoint.
/// </summary>
/// <param name="LibraryId">The stable library ID whose mappings were updated.</param>
/// <param name="RepositoryIds">The repository IDs currently mapped to the library.</param>
public sealed record LibraryRepositoryMappingsResponse(string LibraryId, string[] RepositoryIds);

/// <summary>
/// Preview response for importing a signed Zeeq library export package.
/// </summary>
/// <param name="DocumentCount">Number of package documents that would be imported.</param>
/// <param name="NewPaths">Paths that do not currently exist in the target library.</param>
/// <param name="DuplicateLocalPaths">Existing local paths that would be overwritten.</param>
/// <param name="BlockedRemotePaths">Existing synced/remote paths that block import.</param>
public sealed record LibraryImportPreviewResponse(
    int DocumentCount,
    string[] NewPaths,
    string[] DuplicateLocalPaths,
    string[] BlockedRemotePaths
);

/// <summary>
/// Import result for a signed Zeeq library export package.
/// </summary>
/// <param name="CreatedCount">Number of local documents created.</param>
/// <param name="UpdatedCount">Number of local documents overwritten.</param>
/// <param name="UpdatedPaths">Existing local paths overwritten by the import.</param>
public sealed record LibraryImportResponse(
    int CreatedCount,
    int UpdatedCount,
    string[] UpdatedPaths
);

/// <summary>
/// Conflict response for import attempts that require explicit user action.
/// </summary>
/// <param name="DuplicateLocalPaths">Local paths that require overwrite confirmation.</param>
/// <param name="BlockedRemotePaths">Synced/remote paths that cannot be overwritten by import.</param>
/// <param name="Message">Human-readable conflict reason.</param>
public sealed record LibraryImportConflictResponse(
    string[] DuplicateLocalPaths,
    string[] BlockedRemotePaths,
    string Message
);

/// <summary>
/// One section or code snippet the indexing pipeline would compose from a document's current
/// content. Mirrors <see cref="Zeeq.Core.Documents.Snippets.ComposedSnippet"/>, minus the
/// embedding payload/hash — this is a read-only preview, not the persisted indexing shape.
/// </summary>
/// <param name="Kind">"section" or "code".</param>
/// <param name="Header">Owning heading text.</param>
/// <param name="HeadingPath">Hierarchical heading path, e.g. "Guide &gt; Install".</param>
/// <param name="Language">Fence language (code only); null for section snippets.</param>
/// <param name="Tag">Resolved fence tag (code only); null for section snippets.</param>
/// <param name="Content">Section prose or code content.</param>
/// <param name="Identifiers">Lowercased extracted identifiers (code only); empty for sections.</param>
/// <param name="TokenCount">Token count of the snippet's embedding payload.</param>
public sealed record DocumentParsePreviewSnippetResponse(
    string Kind,
    string Header,
    string HeadingPath,
    string? Language,
    string? Tag,
    string Content,
    string[] Identifiers,
    int TokenCount
);

/// <summary>
/// Preview of what the parse and snippet-indexing pipeline would extract from a document's
/// current content — the title/keywords/headings that would be stored, and the section/code
/// snippets that would be composed for secondary indexing. Nothing is persisted; this runs the
/// same pure parse/compose steps the write path uses, on demand.
/// </summary>
/// <param name="Path">Normalized document path.</param>
/// <param name="Title">Resolved title: front-matter title → first H1 → file name.</param>
/// <param name="Keywords">Normalized keywords derived from front matter.</param>
/// <param name="Headings">Plain heading text, in document order.</param>
/// <param name="Snippets">Composed section and code snippets, in document order.</param>
public sealed record DocumentParsePreviewResponse(
    string Path,
    string Title,
    string[] Keywords,
    string[] Headings,
    DocumentParsePreviewSnippetResponse[] Snippets
);

/// <summary>
/// Error response for library endpoint validation failures.
/// </summary>
/// <param name="Message">Human-readable validation error.</param>
public sealed record LibraryError(string Message);

/// <summary>
/// Error response for document endpoint validation failures.
/// </summary>
/// <param name="Message">Human-readable validation error.</param>
public sealed record DocumentError(string Message);
