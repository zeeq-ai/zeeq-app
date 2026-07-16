namespace Zeeq.Core.Models;

/// <summary>
/// Current assignment state for a pull request in Zeeq.
/// </summary>
public enum PullRequestClaimStatus
{
    /// <summary>
    /// No Zeeq user has claimed the pull request.
    /// </summary>
    Unclaimed,

    /// <summary>
    /// A Zeeq user has claimed the pull request for review work.
    /// </summary>
    Claimed,
}
