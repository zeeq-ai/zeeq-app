namespace Zeeq.Core.Models;

/// <summary>
/// Durable per-organization execution slot for a running code review.
/// </summary>
/// <remarks>
/// The primary key is <c>(OrganizationId, SlotIndex)</c>. Slot count is bounded
/// by the effective organization setting, while the lease row records the exact
/// partitioned review and pull request currently occupying that slot.
/// </remarks>
public sealed class CodeReviewExecutionLease : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Optional team context for the executing review.</summary>
    public string? TeamId { get; init; }

    /// <summary>Zero-based organization execution slot index.</summary>
    public required int SlotIndex { get; init; }

    /// <summary>Opaque lease id used by workers when renewing or releasing the slot.</summary>
    public required string LeaseId { get; init; }

    /// <summary>Repository mapping id for the review.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Pull request record occupying this execution slot.</summary>
    public required string PullRequestRecordId { get; init; }

    /// <summary>Partition key for the pull request record.</summary>
    public required DateTimeOffset PullRequestCreatedAtUtc { get; init; }

    /// <summary>Code review record occupying this execution slot.</summary>
    public required string CodeReviewRecordId { get; init; }

    /// <summary>Partition key for the code review record.</summary>
    public required DateTimeOffset CodeReviewCreatedAtUtc { get; init; }

    /// <summary>UTC time when this lease was acquired.</summary>
    public required DateTimeOffset AcquiredAtUtc { get; init; }

    /// <summary>UTC time when this lease was last renewed.</summary>
    public required DateTimeOffset RenewedAtUtc { get; set; }

    /// <summary>UTC time when this lease may be considered abandoned.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; set; }

    /// <summary>Optional process or host identifier that owns the lease.</summary>
    public string? WorkerId { get; set; }
}
