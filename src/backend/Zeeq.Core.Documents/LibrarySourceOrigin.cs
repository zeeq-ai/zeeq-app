namespace Zeeq.Core.Documents;

/// <summary>
/// Placeholder for future external ingestion origin metadata.
/// </summary>
/// <param name="Kind">External source kind, such as GitHub or GitLab.</param>
/// <param name="RepoUrl">Repository URL used as the library source.</param>
/// <param name="IncludeFilter">Optional glob pattern for included documents.</param>
/// <param name="ExcludeFilter">Optional glob pattern for excluded documents.</param>
public sealed record LibrarySourceOrigin(
    string Kind,
    string RepoUrl,
    string? IncludeFilter,
    string? ExcludeFilter
);
