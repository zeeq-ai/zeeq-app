using Microsoft.Extensions.Logging;
using Octokit;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Outcome of re-checking whether a public repository is still publicly accessible.
/// </summary>
public enum RepositoryVisibilityCheckResult
{
    /// <summary>The repository is still public.</summary>
    Public,

    /// <summary>
    /// The repository is no longer publicly accessible — either it went
    /// private, or it was deleted/renamed. An anonymous check cannot tell
    /// these apart (GitHub returns 404 for both), so both are treated the
    /// same: no longer safe to serve through the shared public table.
    /// </summary>
    NotPubliclyAccessible,

    /// <summary>
    /// The check itself failed for a reason unrelated to visibility (rate
    /// limit, network error, transient 5xx). Not a signal to quarantine —
    /// retry on the next scheduled sync.
    /// </summary>
    TransientError,
}

/// <summary>
/// Re-verifies a public source's upstream visibility before each sync (spec §13).
/// </summary>
/// <remarks>
/// Public sources are ingested anonymously (no GitHub App installation is
/// guaranteed to exist for an arbitrary public source's owner), so this check
/// is also anonymous — it only ever sees what an unauthenticated caller would.
/// That is exactly the visibility re-check needs: whether the repository is
/// still servable to Zeeq without any credential.
/// </remarks>
public interface IPublicRepositoryVisibilityChecker
{
    /// <summary>Checks whether <paramref name="repoUrl"/> is still publicly accessible.</summary>
    Task<RepositoryVisibilityCheckResult> CheckAsync(
        string repoUrl,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Narrow seam around the single Octokit call this needs, mirroring
/// <c>IGitHubInstallationTokenClient</c>'s pattern of keeping the actual
/// network call behind a small interface so callers can be tested without
/// mocking the broad Octokit client graph.
/// </summary>
internal interface IGitHubRepositoryVisibilityClient
{
    /// <summary>Returns whether the repository is private. Throws <see cref="NotFoundException"/> if inaccessible.</summary>
    Task<bool> IsPrivateAsync(string owner, string name, CancellationToken cancellationToken);
}

internal sealed class OctokitGitHubRepositoryVisibilityClient(
    GitHubConnectionFactory connectionFactory
) : IGitHubRepositoryVisibilityClient
{
    public async Task<bool> IsPrivateAsync(
        string owner,
        string name,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Deliberately unauthenticated — see the type's remarks.
        var client = connectionFactory.CreateAnonymousClient();
        var repository = await client.Repository.Get(owner, name);

        return repository.Private;
    }
}

/// <inheritdoc cref="IPublicRepositoryVisibilityChecker" />
internal sealed partial class PublicRepositoryVisibilityChecker(
    IGitHubRepositoryVisibilityClient client,
    ILogger<PublicRepositoryVisibilityChecker> logger
) : IPublicRepositoryVisibilityChecker
{
    /// <inheritdoc />
    public async Task<RepositoryVisibilityCheckResult> CheckAsync(
        string repoUrl,
        CancellationToken cancellationToken
    )
    {
        if (!TryParseOwnerAndName(repoUrl, out var owner, out var name))
        {
            LogUnparseableUrl(logger, repoUrl);
            return RepositoryVisibilityCheckResult.TransientError;
        }

        try
        {
            var isPrivate = await client.IsPrivateAsync(owner, name, cancellationToken);

            return isPrivate
                ? RepositoryVisibilityCheckResult.NotPubliclyAccessible
                : RepositoryVisibilityCheckResult.Public;
        }
        catch (NotFoundException)
        {
            // NOTE: quarantines on a single 404 sample, no retry/confirmation
            // window. Deliberate: every sync re-checks visibility anyway, so a
            // spurious 404 (rare GitHub-side eventual-consistency blip) only
            // costs one cycle of suspended sync, not permanent data loss —
            // documents are frozen, not deleted, on quarantine. A
            // consecutive-failure threshold would reduce false-positive
            // quarantines but needs new persisted state (a counter column) and
            // was judged out of scope here; revisit if false positives are
            // observed in practice.
            return RepositoryVisibilityCheckResult.NotPubliclyAccessible;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckFailed(logger, ex, repoUrl);
            return RepositoryVisibilityCheckResult.TransientError;
        }
    }

    /// <summary>Parses <c>owner</c>/<c>name</c> out of a canonical GitHub repo URL.</summary>
    private static bool TryParseOwnerAndName(string repoUrl, out string owner, out string name)
    {
        owner = "";
        name = "";

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        owner = segments[0];
        name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];

        return true;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not parse owner/repo from public source URL {RepoUrl} for visibility check; treating as transient."
    )]
    private static partial void LogUnparseableUrl(ILogger logger, string repoUrl);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Public repository visibility check failed for {RepoUrl}; treating as transient, not quarantining."
    )]
    private static partial void LogCheckFailed(ILogger logger, Exception ex, string repoUrl);
}
