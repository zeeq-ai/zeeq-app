namespace Zeeq.Core.Documents;

/// <summary>
/// Execution status for a single repository ingest run.
/// </summary>
public enum IngestRunStatus
{
    /// <summary>
    /// The run is actively processing files.
    /// </summary>
    Running,

    /// <summary>
    /// All files processed without failure; deletion sweep completed.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Some files succeeded but at least one failed; sweep was skipped.
    /// </summary>
    Partial,

    /// <summary>
    /// A fatal error prevented the run from completing any work.
    /// </summary>
    Failed,

    /// <summary>
    /// The run was left active past the allowed lease window and was cleared by recovery.
    /// </summary>
    Stalled,
}
