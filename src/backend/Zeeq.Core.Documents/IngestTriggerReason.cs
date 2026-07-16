namespace Zeeq.Core.Documents;

/// <summary>
/// What initiated a repository ingest run.
/// </summary>
public enum IngestTriggerReason
{
    /// <summary>
    /// The periodic scheduler dispatched this run.
    /// </summary>
    Scheduled,

    /// <summary>
    /// An API call manually triggered this run.
    /// </summary>
    Manual,
}
