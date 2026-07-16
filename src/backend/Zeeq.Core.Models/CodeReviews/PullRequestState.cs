namespace Zeeq.Core.Models;

/// <summary>
/// Durable state of a pull request as reported by the provider.
/// </summary>
public enum PullRequestState
{
    /// <summary>
    /// Pull request is open and can still receive updates.
    /// </summary>
    Open = 0,

    /// <summary>
    /// Pull request was closed without being merged.
    /// </summary>
    Closed = 1,

    /// <summary>
    /// Pull request was merged into its target branch.
    /// </summary>
    Merged = 2,
}
