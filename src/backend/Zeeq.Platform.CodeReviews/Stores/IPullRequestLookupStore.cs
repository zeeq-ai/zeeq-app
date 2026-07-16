using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for non-partitioned pull request lookup and uniqueness rows.
/// </summary>
/// <remarks>
/// Pull request records are partitioned by creation time, so cross-partition
/// uniqueness for provider PR identity lives here. This store maps
/// organization, repository, and pull request number to the current partitioned
/// row ID plus creation timestamp.
/// </remarks>
public interface IPullRequestLookupStore
{
    /// <summary>
    /// Finds the lookup row for a provider pull request identity.
    /// </summary>
    /// <remarks>
    /// Webhook handlers use this before loading the partitioned PR record.
    /// Missing rows mean this is the first observed durable PR state for that
    /// provider identity.
    /// </remarks>
    Task<PullRequestLookup?> FindAsync(
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates or updates the lookup pointer for a provider pull request.
    /// </summary>
    /// <remarks>
    /// Implementations should serialize competing writers for the same PR and
    /// preserve the durable uniqueness invariant. In Postgres this is backed by
    /// a non-partitioned primary key plus transaction-scoped advisory locking.
    /// </remarks>
    Task<PullRequestLookup> UpsertAsync(
        PullRequestLookup lookup,
        CancellationToken cancellationToken
    );
}
