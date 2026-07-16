namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Reaction content values Zeeq sends to GitHub.
/// </summary>
/// <remarks>
/// GitHub's REST reactions API uses string content values such as
/// <c>+1</c>, <c>heart</c>, and <c>eyes</c>. Keeping these as named constants
/// makes message construction clear and leaves room for later policy to choose
/// a different acknowledgement reaction without changing the queue contract.
/// </remarks>
public static class GitHubCommentReactionContent
{
    /// <summary>Thumbs-up acknowledgement reaction.</summary>
    public const string PlusOne = "+1";
}
