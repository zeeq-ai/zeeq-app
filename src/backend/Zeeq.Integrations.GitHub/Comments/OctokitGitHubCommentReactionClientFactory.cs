using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Creates Octokit-backed reaction clients for immediate feedback acknowledgement work.
/// </summary>
/// <remarks>
/// This factory mirrors <see cref="OctokitGitHubCommentClientFactory"/> but keeps
/// reaction writes behind their own interface. Comment rendering needs DOM-aware
/// APIs, while reactions only need a tiny endpoint selection surface. Keeping
/// them separate prevents the lightweight reaction path from inheriting comment
/// rendering concerns such as anchors and leases.
/// </remarks>
internal sealed class OctokitGitHubCommentReactionClientFactory(IGitHubClientFactory clientFactory)
    : IGitHubCommentReactionClientFactory
{
    /// <inheritdoc />
    public async Task<IGitHubCommentReactionClient> CreateForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var client = await clientFactory.CreateInstallationClientForOrganizationAsync(
            organizationId,
            cancellationToken
        );

        return new OctokitGitHubCommentReactionClient(client);
    }
}
