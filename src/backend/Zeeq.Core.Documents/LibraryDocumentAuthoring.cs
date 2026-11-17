namespace Zeeq.Core.Documents;

/// <summary>Identifies who owns the document content.</summary>
public enum LibraryDocumentAuthoring
{
    /// <summary>Locally authored by a person.</summary>
    Manual = 0,

    /// <summary>Locally authored by an agent.</summary>
    Agent = 1,

    /// <summary>Owned by an external synchronization source.</summary>
    Synced = 2,
}
