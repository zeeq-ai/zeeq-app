namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Minimal GitHub comment data needed by the resolver and writer.
/// </summary>
/// <remarks>
/// This keeps platform rendering logic independent from Octokit response
/// models. The GitHub integration adapter is responsible for translating issue
/// comments and pull request review comments into this small shape.
/// </remarks>
public sealed record GitHubCommentCandidate(long CommentId, string Body);
