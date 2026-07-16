namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Stable identity for a user-authored GitHub comment that should receive a reaction.
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="GitHubCommentTargetSelector" />.
/// Rendered comment selectors identify Zeeq-owned document targets that need
/// leases, anchors, and DOM parsing. Reaction targets identify user-authored
/// GitHub comments and only need the comment surface plus the GitHub comment id.
/// </remarks>
public sealed record GitHubCommentReactionTarget(
    GitHubCommentReactionTargetKind Kind,
    long CommentId
);
