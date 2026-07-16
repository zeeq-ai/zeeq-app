namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for acquiring, renewing, and releasing GitHub comment writer leases.
/// </summary>
/// <remarks>
/// The lease serializes read-modify-write cycles against a GitHub comment so
/// competing queue consumers cannot overwrite each other. Implementations should
/// keep acquire operations short and must not hold database transactions open
/// while a caller performs GitHub network calls.
///
/// This interface is intentionally portable. If Zeeq later adds a shared
/// coordination package, the contract can move there without changing the
/// comment writer's control flow.
/// </remarks>
public interface IGitHubCommentLeaseStore
{
    /// <summary>
    /// Attempts to acquire a lease for the target comment.
    /// </summary>
    /// <returns><c>true</c> when this worker owns the lease; otherwise <c>false</c>.</returns>
    Task<bool> TryAcquireAsync(
        GitHubCommentLeaseKey key,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Extends the lease while the current worker is still processing.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the lease was renewed; <c>false</c> when the lease was
    /// missing or owned by a different worker.
    /// </returns>
    Task<bool> RenewAsync(
        GitHubCommentLeaseKey key,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Releases the lease when the worker has finished or abandoned the write.
    /// </summary>
    /// <remarks>
    /// Release is owner-checked. A stale worker must not delete a lease that a
    /// newer worker acquired after timeout.
    /// </remarks>
    Task ReleaseAsync(
        GitHubCommentLeaseKey key,
        string workerId,
        CancellationToken cancellationToken
    );
}
