using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Shared marker names and default rank values used in Zeeq-rendered GitHub comments.
/// </summary>
/// <remarks>
/// Start markers include an order key, for example
/// <c>&lt;!-- (800000):zeeq:pr-findings:start --&gt;</c>. End markers do not
/// include the order key so moving a section only rewrites the start marker.
/// </remarks>
public static class GitHubCommentMarkers
{
    /// <summary>Root marker for the persistent pull request summary comment.</summary>
    public const string PullRequestRoot = "zeeq:pr-comment-root";

    /// <summary>Scope key used by the single persistent top-level PR summary comment.</summary>
    public const string PullRequestSummaryScopeKey = "root";

    /// <summary>Header section rendered near the top of a comment.</summary>
    public const string PullRequestHeader = "zeeq:pr-comment-header";

    /// <summary>Status section for queued, running, completed, or blocked states.</summary>
    public const string PullRequestStatus = "zeeq:pr-comment-status";

    /// <summary>Action links such as "request another review".</summary>
    public const string PullRequestActions = "zeeq:pr-actions";

    /// <summary>Review findings summary or detailed findings body.</summary>
    public const string PullRequestFindings = "zeeq:pr-findings";

    /// <summary>Evidence section for supporting review details.</summary>
    public const string PullRequestEvidence = "zeeq:pr-evidence";

    /// <summary>Sources/telemetry section: KB documents and snippets consulted during the review.</summary>
    public const string PullRequestSources = "zeeq:pr-sources";

    /// <summary>Footer section rendered at the bottom of the Zeeq comment.</summary>
    public const string PullRequestFooter = "zeeq:pr-footer";

    /// <summary>Rank reserved for the root marker.</summary>
    public const string RootOrderKey = "000000";

    /// <summary>Rank reserved for the footer marker.</summary>
    public const string FooterOrderKey = "zzzzzz";

    /// <summary>
    /// Default order keys for first-party Zeeq sections.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultOrderKeys { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PullRequestHeader] = "100000",
            [PullRequestStatus] = "200000",
            [PullRequestFindings] = "300000",
            [PullRequestEvidence] = "900000",
            [PullRequestSources] = "925000",
            [PullRequestActions] = "950000",
            [PullRequestFooter] = FooterOrderKey,
        };

    /// <summary>
    /// Gets the root marker for a target selector.
    /// </summary>
    /// <param name="target">GitHub comment target being rendered.</param>
    /// <returns>The root marker name used for the target's DOM.</returns>
    public static string RootFor(GitHubCommentTargetSelector target) =>
        target.Kind switch
        {
            GitHubCommentTargetKind.PullRequestSummary => PullRequestRoot,
            GitHubCommentTargetKind.ReviewThread => "zeeq:review-thread-comment-root",
            GitHubCommentTargetKind.StandaloneIssueComment => "zeeq:standalone-comment-root",
            _ => PullRequestRoot,
        };
}
