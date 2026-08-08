namespace Zeeq.Core.Documents;

/// <summary>
/// Which content set an externally-sourced ingest run processes.
/// </summary>
public enum ExternalSyncScope
{
    /// <summary>Process only items claimed from <c>docs_external_pending_content_syncs</c>.</summary>
    Incremental = 0,

    /// <summary>Re-enumerate every visible item via the provider's search/listing API, then sweep unstamped documents.</summary>
    Full = 1,
}
