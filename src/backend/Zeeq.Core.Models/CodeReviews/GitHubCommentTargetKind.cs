namespace Zeeq.Core.Models;

/// <summary>
/// Identifies the logical GitHub comment surface that Zeeq should render into.
/// </summary>
/// <remarks>
/// Zeeq uses one rendering pipeline for several GitHub comment surfaces. The
/// target kind tells the resolver and writer which GitHub API family owns the
/// comment: issue comments for PR timeline comments, and pull request review
/// comments for diff-thread comments. Keeping this enum in the model layer lets
/// the durable anchor table store the same target identity used by renderer
/// messages without coupling the model to Octokit or GitHub HTTP details.
/// </remarks>
public enum GitHubCommentTargetKind
{
    /// <summary>
    /// The persistent summary comment on the pull request issue timeline.
    /// </summary>
    PullRequestSummary = 1,

    /// <summary>
    /// A comment that belongs to a pull request review thread.
    /// </summary>
    ReviewThread = 2,

    /// <summary>
    /// A standalone issue comment that is not the persistent PR summary.
    /// </summary>
    StandaloneIssueComment = 3,
}
