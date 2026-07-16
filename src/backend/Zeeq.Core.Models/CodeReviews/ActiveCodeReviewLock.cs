namespace Zeeq.Core.Models;

/// <summary>
/// Non-partitioned guard row for one active review per pull request.
/// </summary>
public sealed class ActiveCodeReviewLock : IOrganizationScopedEntity, IUpdatedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Optional Zeeq team context for this review.
    /// </summary>
    public string? TeamId { get; init; }

    /// <summary>
    /// Local repository mapping id.
    /// </summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Pull request record guarded by this active review lock.
    /// </summary>
    public required string PullRequestRecordId { get; init; }

    /// <summary>
    /// Partition key for the pull request record.
    /// </summary>
    public DateTimeOffset PullRequestCreatedAtUtc { get; init; }

    /// <summary>
    /// Active code review record id.
    /// </summary>
    public required string CodeReviewRecordId { get; init; }

    /// <summary>
    /// Partition key for the code review record.
    /// </summary>
    public DateTimeOffset CodeReviewCreatedAtUtc { get; init; }

    /// <summary>
    /// Active review status, normally pending or running.
    /// </summary>
    public CodeReviewStatus Status { get; set; }

    /// <summary>
    /// UTC time when the guard was acquired.
    /// </summary>
    public DateTimeOffset AcquiredAtUtc { get; init; }

    /// <summary>
    /// UTC time when the guard may be considered abandoned and self-healed.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; set; }

    /// <inheritdoc />
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
