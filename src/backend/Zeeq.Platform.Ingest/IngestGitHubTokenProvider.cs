using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Core.Documents.Dispatch;
using Zeeq.Integrations.GitHub;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Resolves GitHub tokens for repository ingest clones.
/// </summary>
/// <remarks>
/// Resolution chain, mirroring the prototype's <c>GitHubTokenService</c>:
/// <list type="number">
///   <item><c>AppSettings.GitHub.AlwaysUseGhTokenForSync</c> → force the <c>GH_TOKEN</c> env var.</item>
///   <item>Private job with an installation id → mint via <see cref="IGitHubInstallationTokenProvider"/> (cached, ~5-min refresh buffer, shared with the API client cache).</item>
///   <item><c>GH_TOKEN</c> env var fallback.</item>
///   <item>Fail fast with an actionable <see cref="InvalidOperationException"/>.</item>
/// </list>
/// Never logs the token value — only installation id or a masked PAT prefix.
/// </remarks>
public sealed partial class IngestGitHubTokenProvider(
    AppSettings appSettings,
    IGitHubInstallationTokenProvider installationTokenProvider,
    ILogger<IngestGitHubTokenProvider> logger
) : IIngestGitHubTokenProvider
{
    /// <inheritdoc />
    public async Task<string?> GetTokenAsync(
        RepositoryIngestJob job,
        CancellationToken cancellationToken
    )
    {
        if (appSettings.GitHub.AlwaysUseGhTokenForSync)
        {
            return GetGhTokenFromEnvironment(job);
        }

        if (job is { Kind: RepositorySourceKind.Private, InstallationId: { } installationId })
        {
            try
            {
                var token = await installationTokenProvider.GetInstallationTokenAsync(
                    installationId,
                    cancellationToken
                );

                LogUsingAppToken(installationId);

                return token;
            }
            catch (Exception ex)
            {
                // NOTE: intentionally broad, including OperationCanceledException —
                // mirrors the prototype's GitHubTokenService fallback chain. Any
                // installation-token failure (expired app credentials, revoked
                // installation, transient API error, or cancellation racing the
                // mint call) falls through to GH_TOKEN rather than failing the
                // whole ingest run, since GH_TOKEN is a valid credential on its
                // own. LogAppTokenError below still surfaces the root cause for
                // diagnosis without blocking the run.
                LogAppTokenError(ex, installationId);
            }
        }

        return GetGhTokenFromEnvironment(job);
    }

    /// <summary>
    /// Reads <c>GH_TOKEN</c>, or resolves the public/private no-token outcome
    /// when it is unset.
    /// </summary>
    /// <remarks>
    /// A public source with no PAT clones anonymously — GitHub allows
    /// unauthenticated HTTPS clones of public repositories, just at a lower
    /// rate limit. A private source with no PAT (and, above, no usable
    /// installation token) cannot clone at all, so that case fails fast
    /// instead of letting an anonymous clone attempt return a confusing 404.
    /// </remarks>
    private string? GetGhTokenFromEnvironment(RepositoryIngestJob job)
    {
        var pat = Environment.GetEnvironmentVariable("GH_TOKEN");

        if (string.IsNullOrWhiteSpace(pat))
        {
            if (job.Kind == RepositorySourceKind.Public)
            {
                LogCloningAnonymously(job.RepoUrl);
                return null;
            }

            LogNoTokenAvailable();

            throw new InvalidOperationException(
                "No GitHub token is available for private repository ingest. Either link a "
                    + "GitHub App installation to this source or set the GH_TOKEN environment variable."
            );
        }

        LogUsingPat(MaskPat(pat));

        return pat;
    }

    private static string MaskPat(string pat) => pat.Length <= 12 ? "****" : pat[..8] + "****";

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "GitHub token: using GitHub App installation token for InstallationId={InstallationId}"
    )]
    private partial void LogUsingAppToken(long installationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "GitHub token: failed to mint installation token for InstallationId={InstallationId} — falling back to GH_TOKEN"
    )]
    private partial void LogAppTokenError(Exception ex, long installationId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "GitHub token: using GH_TOKEN environment variable (PAT: {MaskedPat})"
    )]
    private partial void LogUsingPat(string maskedPat);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "GitHub token: no token source is available for repository ingest."
    )]
    private partial void LogNoTokenAvailable();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "GitHub token: no PAT configured — cloning public repository {RepoUrl} anonymously"
    )]
    private partial void LogCloningAnonymously(string repoUrl);
}
