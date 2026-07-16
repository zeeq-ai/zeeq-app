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
}
