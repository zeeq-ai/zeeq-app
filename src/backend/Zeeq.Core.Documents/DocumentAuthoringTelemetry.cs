using System.Diagnostics.Metrics;
using Zeeq.Core.Common;

namespace Zeeq.Core.Documents;

/// <summary>Metrics shared by document authoring and ingestion.</summary>
public static class DocumentAuthoringTelemetry
{
    /// <summary>Sync paths skipped because a local document owns them.</summary>
    public static readonly Counter<int> SyncSkippedLocal = ZeeqTelemetry.Metrics.CreateCounter<int>(
        "zeeq_document_sync_skipped_local_total"
    );
}
