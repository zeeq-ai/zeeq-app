namespace Zeeq.Core.Documents;

/// <summary>
/// Tracks which external source a document was ingested from.
/// </summary>
/// <param name="Kind">External source kind.</param>
/// <param name="RepoRef">Repository reference used during ingestion.</param>
/// <param name="SourcePath">Original case-preserving repository-relative path, when known.</param>
public sealed record LibraryDocumentSourceOrigin(
    string Kind,
    string RepoRef,
    string? SourcePath = null
);
