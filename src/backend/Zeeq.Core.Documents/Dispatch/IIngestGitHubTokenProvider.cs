namespace Zeeq.Core.Documents.Dispatch;

/// <summary>
/// Resolves a GitHub token for cloning a repository during an ingest run.
/// </summary>
/// <remarks>
/// Resolution chain (see <c>Zeeq.Platform.Ingest.IngestGitHubTokenProvider</c>
/// for the implementation): forced <c>GH_TOKEN</c> override for local/CI, then a
/// GitHub App installation token for private sources, then a <c>GH_TOKEN</c>
/// fallback. Never log the returned token value.
/// </remarks>
public interface IIngestGitHubTokenProvider
{
    /// <summary>
    /// Resolves a token usable for <c>git</c>/<c>gh</c> operations against the
    /// given job's repository, or <see langword="null"/> when
    /// <see cref="RepositorySourceKind.Public"/> and no token is configured —
    /// public repositories clone anonymously over HTTPS without one. A
    /// <see cref="RepositorySourceKind.Private"/> job with no resolvable token
    /// is a fail-fast <see cref="InvalidOperationException"/> instead, since an
    /// anonymous clone of a private repository cannot succeed.
    /// </summary>
    Task<string?> GetTokenAsync(RepositoryIngestJob job, CancellationToken cancellationToken);
}
