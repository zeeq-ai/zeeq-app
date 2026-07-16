namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Resolves the existing Zeeq-owned GitHub comment for a target.
/// </summary>
/// <remarks>
/// Resolution is the read side of the GitHub comment pipeline. It first tries
/// the durable anchor's stored comment id. If that id is missing or stale, it
/// scans GitHub comments for Zeeq's target root marker and returns the parsed
/// DOM from the live body. Returning <c>null</c> means the writer should create
/// the first comment for the target.
/// </remarks>
public interface IGitHubCommentResolver
{
    /// <summary>
    /// Locates the Zeeq-owned comment for the target, if one already exists.
    /// </summary>
    Task<GitHubCommentResolution?> ResolveAsync(
        IGitHubCommentClient client,
        GitHubCommentTargetSelector target,
        string ownerQualifiedRepoName,
        long? storedCommentId,
        CancellationToken cancellationToken
    );
}
