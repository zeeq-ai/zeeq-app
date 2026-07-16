using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Core.Documents.Dispatch;

/// <summary>
/// A unit of work handed to a dispatcher for execution.
/// </summary>
/// <remarks>
/// Carries everything <see cref="IRepositoryIngestDispatcher"/> and
/// `RepositoryIngestRunner` need to run one repository sync end to
/// end. Instances are serializable so out-of-process runtimes (isolated process,
/// Cloud Run Job) can pass them across the process boundary as JSON.
/// </remarks>
public sealed record RepositoryIngestJob
{
    /// <summary>Opaque run identifier (UUIDv7). Also the <c>sync_run_id</c> stamped on documents.</summary>
    public required string RunId { get; init; }

    /// <summary>Partition key for the run record.</summary>
    public required DateTimeOffset RunCreatedAtUtc { get; init; }

    /// <summary>Public or private source.</summary>
    public required RepositorySourceKind Kind { get; init; }

    /// <summary>Canonical repository URL being ingested.</summary>
    public required string RepoUrl { get; init; }

    /// <summary>
    /// What initiated this run — carried through to the run record. Not in the
    /// spec's original sketch of this type (§4.1); added because the runner
    /// needs it to populate <c>DocsIngestRun.Trigger</c> and it has nowhere
    /// else to come from once a job crosses a process boundary.
    /// </summary>
    public required IngestTriggerReason Trigger { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="RepositorySourceKind.Public"/>.</summary>
    public string? PublicSourceId { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="RepositorySourceKind.Private"/>.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="RepositorySourceKind.Private"/>.</summary>
    public string? LibraryId { get; init; }

    /// <summary>
    /// Optional team scope, set when <see cref="Kind"/> is <see cref="RepositorySourceKind.Private"/>
    /// and the library is team-owned. Carried through so the runner can stamp
    /// <c>LibraryDocument.TeamId</c> without a separate library lookup.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>GitHub App installation id, set when <see cref="Kind"/> is <see cref="RepositorySourceKind.Private"/>.</summary>
    public long? InstallationId { get; init; }

    /// <summary>Effective include/exclude filter — union of subscribing filters for public sources.</summary>
    public required EffectiveFilter Filter { get; init; }

    /// <summary>Root trace context so the runner's spans attach to the triggering workflow.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}

/// <summary>
/// Resolved include/exclude glob filter for one ingest run.
/// </summary>
/// <param name="IncludeGlobs">Include patterns; empty means "everything not excluded".</param>
/// <param name="ExcludeGlobs">Exclude patterns applied after include matching.</param>
public sealed record EffectiveFilter(string[] IncludeGlobs, string[] ExcludeGlobs)
{
    /// <summary>No filtering beyond the default Markdown-extension narrowing.</summary>
    public static readonly EffectiveFilter Empty = new([], []);
}
