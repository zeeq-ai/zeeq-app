using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Lists repositories accessible to the Zeeq GitHub App installation.
/// </summary>
/// <remarks>
/// Repository registration must be based on the installation's current GitHub
/// repository access, not on arbitrary user input. The management endpoint uses
/// this provider before creating a <see cref="Zeeq.Core.Models.CodeRepository"/>
/// row so webhook ingress only accepts repositories GitHub can actually deliver
/// for the linked installation.
/// </remarks>
public interface IGitHubRepositoryProvider
{
    /// <summary>
    /// Lists repositories currently visible to the GitHub App installation for the organization.
    /// </summary>
    Task<IReadOnlyList<GitHubAvailableRepository>> ListAvailableAsync(
        string organizationId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Octokit implementation of <see cref="IGitHubRepositoryProvider"/>.
/// </summary>
/// <remarks>
/// The client created by <see cref="IGitHubClientFactory"/> uses an installation
/// access token. GitHub's "list repositories accessible to the app installation"
/// endpoint is therefore available through <c>GitHubApps.Installation</c> on
/// that client. Pagination is explicit so large installations do not silently
/// show only the first page in the Zeeq settings UI.
/// </remarks>
internal sealed class OctokitGitHubRepositoryProvider(IGitHubClientFactory clientFactory)
    : IGitHubRepositoryProvider
{
    private const int PageSize = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubAvailableRepository>> ListAvailableAsync(
        string organizationId,
        CancellationToken cancellationToken
    )
    {
        var client = await clientFactory.CreateInstallationClientForOrganizationAsync(
            organizationId,
            cancellationToken
        );
        var repositories = new List<GitHubAvailableRepository>();
        var page = 1;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await client.GitHubApps.Installation.GetAllRepositoriesForCurrent(
                new ApiOptions
                {
                    StartPage = page,
                    PageSize = PageSize,
                    PageCount = 1,
                }
            );

            repositories.AddRange(response.Repositories.Select(ToAvailableRepository));

            if (response.Repositories.Count < PageSize)
            {
                break;
            }

            page++;
        }

        return
        [
            .. repositories.OrderBy(
                repository => repository.OwnerQualifiedName,
                StringComparer.OrdinalIgnoreCase
            ),
        ];
    }

    private static GitHubAvailableRepository ToAvailableRepository(Repository repository) =>
        new(
            GitHubRepositoryId: repository.Id,
            NodeId: repository.NodeId ?? string.Empty,
            Name: repository.Name,
            OwnerQualifiedName: repository.FullName,
            Private: repository.Private,
            DefaultBranch: repository.DefaultBranch ?? string.Empty,
            HtmlUrl: repository.HtmlUrl
        );
}
