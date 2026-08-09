using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest;

/// <summary>Resolves current Notion credentials and executes an ingest job.</summary>
public interface INotionIngestDispatcher
{
    /// <summary>Runs the job and returns its durable terminal status.</summary>
    Task<NotionDispatchOutcome> RunAsync(NotionIngestJob job, CancellationToken cancellationToken);
}

/// <summary>Terminal outcome of dispatching one Notion ingest job.</summary>
public sealed record NotionDispatchOutcome(IngestRunStatus Status, string? FailureReason = null);
