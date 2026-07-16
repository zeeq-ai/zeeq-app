using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Creates Octokit-backed comment clients for the platform comment writer.
/// </summary>
/// <remarks>
/// This adapter is intentionally tiny. The platform queue handler depends on
/// <see cref="IGitHubCommentClientFactory" /> so it can stay free of Octokit and
/// GitHub App authentication details. The GitHub integration layer resolves the
/// installation-authenticated Octokit client and wraps it in
/// <see cref="OctokitGitHubCommentClient" /> for resolver/writer use.
/// </remarks>
internal sealed class OctokitGitHubCommentClientFactory(IGitHubClientFactory clientFactory)
    : IGitHubCommentClientFactory
{
    /// <inheritdoc />
    public async Task<IGitHubCommentClient> CreateForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var client = await clientFactory.CreateInstallationClientForOrganizationAsync(
            organizationId,
            cancellationToken
        );

        return new OctokitGitHubCommentClient(client);
    }
}
