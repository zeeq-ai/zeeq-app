namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Creates the minimal GitHub comment client used by the comment writer pipeline.
/// </summary>
/// <remarks>
/// The queue handler lives in the provider-neutral code-review platform package,
/// so it cannot depend on Octokit or the GitHub integration project. This small
/// factory is the seam between the platform workflow and the GitHub adapter: the
/// handler asks for a comment client for the organization, and the integration
/// layer decides how to authenticate and adapt GitHub's API surface.
/// </remarks>
public interface IGitHubCommentClientFactory
{
    /// <summary>
    /// Creates a GitHub comment client authenticated for the organization.
    /// </summary>
    /// <param name="organizationId">Zeeq organization id that owns the GitHub App installation.</param>
    /// <param name="cancellationToken">Cancels local lookup and token/client creation work.</param>
    /// <returns>The small comment API surface used by the resolver and writer.</returns>
    Task<IGitHubCommentClient> CreateForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    );
}
