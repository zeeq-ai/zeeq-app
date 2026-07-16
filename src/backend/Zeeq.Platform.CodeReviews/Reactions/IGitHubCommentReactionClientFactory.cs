namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Creates GitHub reaction clients for a Zeeq organization.
/// </summary>
/// <remarks>
/// Reaction writes use the same GitHub App installation authentication model as
/// rendered comment writes. The platform handler asks for a client by Zeeq
/// organization id and the GitHub integration layer resolves the installation
/// token through the existing GitHub client factory.
/// </remarks>
public interface IGitHubCommentReactionClientFactory
{
    /// <summary>
    /// Creates a reaction client authenticated for the organization's GitHub installation.
    /// </summary>
    /// <param name="organizationId">Zeeq organization id that owns the repository mapping.</param>
    /// <param name="cancellationToken">Cancellation token for client creation.</param>
    /// <returns>A provider-specific reaction client behind the platform interface.</returns>
    Task<IGitHubCommentReactionClient> CreateForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken
    );
}
