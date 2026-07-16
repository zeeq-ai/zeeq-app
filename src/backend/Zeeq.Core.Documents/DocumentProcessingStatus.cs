namespace Zeeq.Core.Documents;

/// <summary>
/// Secondary processing state for a library document.
/// </summary>
public enum DocumentProcessingStatus
{
    /// <summary>
    /// The document needs secondary indexing.
    /// </summary>
    Pending,

    /// <summary>
    /// The document has been claimed by the sweep and is being indexed. A stale
    /// <c>Indexing</c> row (not updated within the configured window) is reclaimable —
    /// this is the crash-recovery marker, not a terminal state.
    /// </summary>
    Indexing,

    /// <summary>
    /// Secondary indexing completed successfully.
    /// </summary>
    Indexed,

    /// <summary>
    /// Secondary indexing failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Secondary indexes are stale and need to be rebuilt.
    /// </summary>
    Stale,
}
