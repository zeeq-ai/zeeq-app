namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Result of asking GitHub to add a reaction.
/// </summary>
/// <remarks>
/// GitHub treats reaction creation as idempotent for the same authenticated
/// actor and reaction content. A new reaction returns <c>201</c>, while an
/// already-present reaction returns <c>200</c>. The handler treats both as
/// success. Validation failures such as <c>422</c> are logged and acknowledged
/// because reaction writes must not block review workflow progress.
/// </remarks>
public enum GitHubCommentReactionWriteOutcome
{
    /// <summary>GitHub accepted the reaction as a newly-created reaction.</summary>
    Created = 1,

    /// <summary>GitHub reported that this app/user already added the reaction.</summary>
    AlreadyExists = 2,

    /// <summary>GitHub rejected the reaction as a validation/no-op case.</summary>
    ValidationFailed = 3,
}
