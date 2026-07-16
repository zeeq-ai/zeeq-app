namespace Zeeq.Core.Models;

/// <summary>
/// Execution status for a code review run.
/// </summary>
public enum CodeReviewStatus
{
    /// <summary>
    /// Review has been accepted but has not started running.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Review work is currently running.
    /// </summary>
    Running = 1,

    /// <summary>
    /// Review completed successfully.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Review stopped because processing failed.
    /// </summary>
    Errored = 3,

    /// <summary>
    /// Review was cancelled before it reached a terminal result.
    /// </summary>
    Cancelled = 4,
}
