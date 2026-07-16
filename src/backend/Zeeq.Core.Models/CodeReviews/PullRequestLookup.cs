namespace Zeeq.Core.Models;

/// <summary>
/// Non-partitioned lookup and guard row for one provider pull request.
/// </summary>
/// <remarks>
/// The partitioned pull request table cannot enforce uniqueness without the
/// partition key. This compact table owns the cross-partition invariant for
/// <c>(OrganizationId, RepositoryId, PullRequestNumber)</c>.
/// </remarks>
public sealed class PullRequestLookup : IOrganizationScopedEntity, IUpdatedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Optional Zeeq team context for this pull request.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// Local repository mapping id.
    /// </summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Provider-qualified repository name.
    /// </summary>
    public required string OwnerQualifiedRepoName { get; set; }

    /// <summary>
    /// Provider pull request number.
    /// </summary>
    public int PullRequestNumber { get; init; }

    /// <summary>
    /// Partitioned pull request record id.
    /// </summary>
    public required string PullRequestRecordId { get; set; }

    /// <summary>
    /// Partition key for the pull request record.
    /// </summary>
    public DateTimeOffset PullRequestCreatedAtUtc { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
