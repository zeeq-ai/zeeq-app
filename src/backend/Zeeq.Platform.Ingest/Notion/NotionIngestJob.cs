using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;

namespace Zeeq.Platform.Ingest;

/// <summary>Immutable inputs for one Notion content ingest run.</summary>
public sealed record NotionIngestJob
{
    /// <summary>Opaque run identifier also stamped on synchronized documents.</summary>
    public required string RunId { get; init; }

    /// <summary>Partition timestamp paired with <see cref="RunId"/>.</summary>
    public required DateTimeOffset RunCreatedAtUtc { get; init; }

    /// <summary>Organization that owns the library.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Optional team that owns the library.</summary>
    public string? TeamId { get; init; }

    /// <summary>Library being synchronized.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Incremental dirty set or full visible-page enumeration.</summary>
    public required ExternalSyncScope Scope { get; init; }

    /// <summary>What initiated this run.</summary>
    public required IngestTriggerReason Trigger { get; init; }

    /// <summary>Effective breadcrumb-path include and exclude filters.</summary>
    public required EffectiveFilter Filter { get; init; }

    /// <summary>Stable non-secret source reference used by legacy repository-named fields.</summary>
    public required string SourceReference { get; init; }

    /// <summary>Trace context captured when the run was queued.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}
