using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Resolves GitHub webhook repository identity into a configured Zeeq repository.
/// </summary>
/// <remarks>
/// GitHub webhook payloads identify repositories as <c>owner/repo</c>. Queue
/// messages need Zeeq tenant context before they can be published, so this
/// gate is the ingress bridge from provider identity to
/// <see cref="CodeRepository"/>. It is intentionally read-only: missing or
/// disabled mappings are acknowledged as no-op by the processor instead of
/// creating rows from unauthenticated webhook input.
///
/// The cache stores a tiny snapshot rather than an EF entity. Repository
/// mapping changes are rare compared with webhook delivery volume, and a short
/// TTL keeps the public webhook route cheap without making configuration
/// changes permanently sticky.
/// </remarks>
public sealed partial class GitHubWebhookRepositoryGate(
    HybridCache cache,
    ICodeRepositoryStore repositories,
    ILogger<GitHubWebhookRepositoryGate> logger
)
{
    private const string Provider = "github";

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(1),
    };

    /// <summary>
    /// Looks up an enabled repository mapping for a GitHub webhook delivery.
    /// </summary>
    /// <param name="ownerQualifiedRepoName">GitHub repository full name.</param>
    /// <param name="deliveryId">GitHub delivery id used only for logs.</param>
    /// <param name="cancellationToken">Cancellation token for cache/store reads.</param>
    /// <returns>A resolved repository mapping, or a missing result.</returns>
    internal async ValueTask<GitHubWebhookRepositoryGateResult> ResolveAsync(
        string ownerQualifiedRepoName,
        string deliveryId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(ownerQualifiedRepoName))
        {
            LogMissingRepositoryName(logger, deliveryId);
            return GitHubWebhookRepositoryGateResult.Missing;
        }

        var normalizedName = ownerQualifiedRepoName.Trim();

        var cacheKey = $"github:webhook:repository:{normalizedName.ToLowerInvariant()}";

        var snapshot = await cache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var repository = await repositories.FindActiveAsync(Provider, normalizedName, ct);

                return RepositoryMappingSnapshot.From(repository);
            },
            CacheOptions,
            tags: [Provider, $"github:repository:{normalizedName.ToLowerInvariant()}"],
            cancellationToken: cancellationToken
        );

        if (!snapshot.Found)
        {
            LogRepositoryMappingNotFound(logger, normalizedName, deliveryId);
            return GitHubWebhookRepositoryGateResult.Missing;
        }

        return GitHubWebhookRepositoryGateResult.Resolved(snapshot.ToRepositoryMapping());
    }

    [LoggerMessage(
        EventId = 3120,
        Level = LogLevel.Information,
        Message = "🪝  GitHub webhook delivery {DeliveryId} had no repository full name; acknowledging as no-op."
    )]
    private static partial void LogMissingRepositoryName(ILogger logger, string deliveryId);

    [LoggerMessage(
        EventId = 3121,
        Level = LogLevel.Information,
        Message = "🪝  GitHub webhook repository {OwnerQualifiedRepoName} delivery {DeliveryId} is not configured/enabled; acknowledging as no-op."
    )]
    private static partial void LogRepositoryMappingNotFound(
        ILogger logger,
        string ownerQualifiedRepoName,
        string deliveryId
    );

    /// <summary>
    /// Cache-safe representation of the active repository mapping.
    /// </summary>
    private sealed record RepositoryMappingSnapshot(
        bool Found,
        string? Id,
        string? OrganizationId,
        string? TeamId,
        string? OwnerQualifiedName
    )
    {
        public static RepositoryMappingSnapshot From(CodeRepository? repository) =>
            repository is null
                ? new(false, null, null, null, null)
                : new(
                    true,
                    repository.Id,
                    repository.OrganizationId,
                    repository.TeamId,
                    repository.OwnerQualifiedName
                );

        public GitHubWebhookRepositoryMapping ToRepositoryMapping() =>
            new(
                Id: Id ?? string.Empty,
                OrganizationId: OrganizationId ?? string.Empty,
                TeamId: TeamId,
                OwnerQualifiedName: OwnerQualifiedName ?? string.Empty
            );
    }
}

/// <summary>
/// Result of resolving a GitHub webhook repository into Zeeq tenant context.
/// </summary>
internal sealed record GitHubWebhookRepositoryGateResult(
    bool IsResolved,
    GitHubWebhookRepositoryMapping? Repository
)
{
    /// <summary>Result used when no configured/enabled mapping exists.</summary>
    public static GitHubWebhookRepositoryGateResult Missing { get; } = new(false, null);

    /// <summary>Creates a successful repository mapping result.</summary>
    public static GitHubWebhookRepositoryGateResult Resolved(
        GitHubWebhookRepositoryMapping repository
    ) => new(true, repository);
}

/// <summary>
/// Minimal repository mapping data needed by webhook ingress.
/// </summary>
internal sealed record GitHubWebhookRepositoryMapping(
    string Id,
    string OrganizationId,
    string? TeamId,
    string OwnerQualifiedName
);
