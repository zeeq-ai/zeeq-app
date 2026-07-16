namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Existing Zeeq-owned GitHub comment resolved for a target.
/// </summary>
/// <remarks>
/// Resolution combines GitHub's external comment id with the parsed DOM from
/// the live comment body. The DOM is read from GitHub every time so renderers
/// preserve sections that were written by earlier signals or future renderer
/// versions. This record intentionally does not store Markdown separately.
/// </remarks>
public sealed record GitHubCommentResolution(long CommentId, GitHubCommentDom Dom);
