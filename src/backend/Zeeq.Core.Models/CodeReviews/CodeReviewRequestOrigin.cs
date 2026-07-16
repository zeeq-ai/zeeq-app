namespace Zeeq.Core.Models;

/// <summary>
/// Source that requested a code review run.
/// </summary>
public enum CodeReviewRequestOrigin
{
    /// <summary>
    /// Review was requested from an incoming repository webhook event.
    /// </summary>
    RepositoryWebhook = 0,

    /// <summary>
    /// Review was requested by an automated Zeeq agent workflow.
    /// </summary>
    Agent = 1,

    /// <summary>
    /// Review was requested directly by a user action.
    /// </summary>
    Manual = 2,
}
