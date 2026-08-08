namespace Zeeq.Core.Documents;

/// <summary>
/// The source origin type for a document ingest source.
/// </summary>
public enum RepositorySourceKind
{
    /// <summary>
    /// A publicly-accessible repository — ingested once, shared globally.
    /// </summary>
    Public,

    /// <summary>
    /// A private, organization-owned repository — ingested per org/library.
    /// </summary>
    Private,

    /// <summary>
    /// A Notion workspace connection — ingested per org/library over REST, page by page.
    /// </summary>
    /// <remarks>
    /// Added to this enum rather than renaming it: the name reads oddly for a non-repository
    /// origin, but this is a persisted discriminator byte referenced across
    /// <see cref="DocsPublicSource"/>, <see cref="DocsIngestRun"/>, and EF configs — a rename
    /// is migration churn with no functional gain. Revisit only if a third non-repository
    /// origin lands.
    /// </remarks>
    Notion = 2,
}
