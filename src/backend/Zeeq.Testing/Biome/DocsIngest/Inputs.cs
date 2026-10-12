using Zeeq.Core.Documents;

namespace Zeeq.Testing.Biomes;

/// <summary>Mutable construction input for DocsPublicSource test output.</summary>
public sealed class DocsPublicSourceInput
{
    /// <summary>Stable source identifier. Auto-generated when omitted.</summary>
    public string? Id { get; set; }

    /// <summary>Canonical repository URL. Auto-generated when omitted.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>Default include globs for subscribing libraries.</summary>
    public string[] DefaultIncludeFilters { get; set; } = [];

    /// <summary>Default exclude globs for subscribing libraries.</summary>
    public string[] DefaultExcludeFilters { get; set; } = [];
}

/// <summary>Mutable construction input for DocsIngestRun test output.</summary>
public sealed class DocsIngestRunInput
{
    /// <summary>Run identifier. Auto-generated when omitted.</summary>
    public string? Id { get; set; }

    /// <summary>Partition key. Defaults to <c>the session clock</c>.</summary>
    public DateTimeOffset? CreatedAtUtc { get; set; }

    /// <summary>Repository visibility kind.</summary>
    public RepositorySourceKind SourceKind { get; set; } = RepositorySourceKind.Private;

    /// <summary>Repository URL.</summary>
    public string? RepoUrl { get; set; }

    /// <summary>Owning organization. Defaults to the seed organization.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>Owning library. Auto-generated when omitted.</summary>
    public string? LibraryId { get; set; }

    /// <summary>Public source reference. Resolved from the typed source parent when omitted
    /// and <see cref="SourceKind"/> is <see cref="RepositorySourceKind.Public"/>.</summary>
    public string? PublicSourceId { get; set; }

    /// <summary>What triggered the run.</summary>
    public IngestTriggerReason Trigger { get; set; } = IngestTriggerReason.Manual;

    /// <summary>Current run status.</summary>
    public IngestRunStatus Status { get; set; } = IngestRunStatus.Running;

    /// <summary>File count totals. Only override what varies.</summary>
    public int FilesTotal { get; set; }

    /// <summary>Files newly added.</summary>
    public int FilesAdded { get; set; }

    /// <summary>Files with changed content.</summary>
    public int FilesUpdated { get; set; }

    /// <summary>Files detected at a new path.</summary>
    public int FilesMoved { get; set; }

    /// <summary>Files unchanged.</summary>
    public int FilesSkipped { get; set; }

    /// <summary>Files removed by sweep.</summary>
    public int FilesDeleted { get; set; }

    /// <summary>Files that failed to process.</summary>
    public int FilesFailed { get; set; }

    /// <summary>Whether this run had an auth failure.</summary>
    public bool AuthFailure { get; set; }

    /// <summary>Failure message for partial/failed runs.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>When the run started. Defaults to <c>CreatedAtUtc</c>.</summary>
    public DateTimeOffset? StartedAtUtc { get; set; }

    /// <summary>When the run completed. Null means still running.</summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

/// <summary>Construction input for a document belonging to a public source.</summary>
public sealed class DocsPublicDocumentInput
{
    /// <summary>Normalized path within the source. Auto-generated when omitted.</summary>
    public string? Path { get; set; }

    /// <summary>Full markdown content. Defaults to a simple one-heading body.</summary>
    public string Content { get; set; } = "# Test\n\nBody.";

    /// <summary>SHA-256 content hash. Auto-generated when omitted.</summary>
    public string? ContentHash { get; set; }

    /// <summary>The sync run ID that stamps this document. Null means not stamped.</summary>
    public string? SyncRunId { get; set; }

    /// <summary>Display title. Resolved from the first heading when omitted.</summary>
    public string? Title { get; set; }

    /// <summary>Prior paths for move detection. Empty by default.</summary>
    public string[] PreviousPaths { get; set; } = [];
}
