using Octokit;
using Octokit.Internal;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Builds Octokit <see cref="GitHubClient"/> instances that share the resilient
/// named HTTP handler registered by <see cref="GitHubResilience"/>.
/// </summary>
/// <remarks>
/// This is the single place that knows how to wire Octokit's transport to the
/// shared resilience pipeline (see <c>Octokit.NET</c>'s
/// <see href="https://octokitnet.readthedocs.io/en/latest/http-client/">HTTP client docs</see>
/// for the <see cref="HttpClientAdapter"/> pattern this follows). Every GitHub API
/// caller in this project should go through here instead of constructing
/// <c>new GitHubClient(...)</c> directly, so retry/backoff behavior for transient
/// GitHub failures lives in one place rather than being duplicated or, worse,
/// silently absent at some call sites.
/// </remarks>
internal sealed class GitHubConnectionFactory(IHttpMessageHandlerFactory handlerFactory)
{
    private static readonly ProductHeaderValue ProductHeader = new("zeeq");

    /// <summary>Creates a <see cref="GitHubClient"/> authenticated with the given credentials.</summary>
    public GitHubClient CreateClient(Credentials credentials) =>
        new(CreateConnection()) { Credentials = credentials };

    /// <summary>Creates an unauthenticated <see cref="GitHubClient"/> for anonymous, public-only calls.</summary>
    public GitHubClient CreateAnonymousClient() => CreateClient(Credentials.Anonymous);

    private Connection CreateConnection() =>
        new(
            ProductHeader,
            new HttpClientAdapter(() => handlerFactory.CreateHandler(GitHubResilience.Name))
        );
}
