using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for organization-scoped code-review execution-capacity leases.
/// </summary>
public interface ICodeReviewExecutionLeaseStore
{
    /// <summary>
    /// Attempts to acquire one execution slot for a review.
    /// </summary>
    Task<CodeReviewExecutionLeaseResult> TryAcquireAsync(
        CodeReviewExecutionLeaseRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Renews a live execution lease.
    /// </summary>
    Task<bool> RenewAsync(
        string leaseId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Releases an execution lease idempotently.
    /// </summary>
    Task ReleaseAsync(string leaseId, CancellationToken cancellationToken);
}

/// <summary>
/// Input required to acquire an organization execution lease.
/// </summary>
public sealed record CodeReviewExecutionLeaseRequest(
    string OrganizationId,
    string? TeamId,
    string RepositoryId,
    string PullRequestRecordId,
    DateTimeOffset PullRequestCreatedAtUtc,
    string CodeReviewRecordId,
    DateTimeOffset CodeReviewCreatedAtUtc,
    int MaxConcurrentReviews,
    TimeSpan LeaseDuration,
    string? WorkerId
);

/// <summary>
/// Three-way organization execution lease acquisition result.
/// </summary>
public enum CodeReviewExecutionLeaseOutcome
{
    /// <summary>A slot was acquired. Run the review.</summary>
    Acquired,

    /// <summary>All organization slots are full. Defer as expected backpressure.</summary>
    NoSlotAvailable,

    /// <summary>A live lease already exists for this exact review. Ack and no-op.</summary>
    AlreadyLeasedForThisReview,
}

/// <summary>
/// Lease acquisition outcome plus the acquired or existing lease when available.
/// </summary>
public sealed record CodeReviewExecutionLeaseResult(
    CodeReviewExecutionLeaseOutcome Outcome,
    CodeReviewExecutionLease? Lease
);
