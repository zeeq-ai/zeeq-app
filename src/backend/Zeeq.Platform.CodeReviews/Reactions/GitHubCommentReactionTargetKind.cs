namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// GitHub comment surfaces that can receive Zeeq acknowledgement reactions.
/// </summary>
/// <remarks>
/// GitHub uses different REST endpoints for PR issue-thread comments and PR
/// review comments. The queue message stores this small discriminator so the
/// provider adapter can select the correct endpoint without rehydrating the
/// original webhook payload.
/// </remarks>
public enum GitHubCommentReactionTargetKind
{
    /// <summary>Reaction target is an issue comment on a pull request conversation.</summary>
    IssueComment = 1,

    /// <summary>Reaction target is a pull request review comment on a diff thread.</summary>
    PullRequestReviewComment = 2,
}
