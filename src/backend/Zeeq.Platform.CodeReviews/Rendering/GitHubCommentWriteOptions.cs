namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runtime timing settings for target-scoped GitHub comment writes.
/// </summary>
/// <remarks>
/// The lease duration is intentionally longer than a typical GitHub comment
/// write, while renewal happens at the halfway point. Production uses a
/// thirty-second lease so a crashed worker clears naturally, and tests can pass a
/// much smaller value to exercise renewal and cancellation without sleeping for
/// minutes. Lease acquisition has its own short wait window because multiple
/// immediate comment signals can target the same root comment at nearly the
/// same time. Waiting briefly lets the second signal run after the first GitHub
/// write releases the lease instead of falling directly into the dead-letter
/// sink.
/// </remarks>
public sealed class GitHubCommentWriteOptions
{
    /// <summary>
    /// Default lease duration for one GitHub comment render/write pass.
    /// </summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a worker owns the comment write slot before renewal is required.
    /// </summary>
    public TimeSpan LeaseDuration { get; init; } = DefaultLeaseDuration;

    /// <summary>
    /// Maximum time to wait for another worker to release the target lease.
    /// </summary>
    /// <remarks>
    /// This is intentionally much shorter than <see cref="LeaseDuration"/>.
    /// Normal contention is caused by back-to-back immediate messages for the
    /// same PR comment and should clear as soon as the first GitHub write
    /// completes. A longer wait would tie up a Brighter performer behind a
    /// stuck lease instead of letting queue retry/dead-letter policy take over.
    /// </remarks>
    public TimeSpan LeaseAcquireTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delay between lease acquisition attempts while another worker owns the target.
    /// </summary>
    public TimeSpan LeaseAcquireRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Delay between successful acquire and each renewal attempt.
    /// </summary>
    /// <remarks>
    /// With the default lease this renews every 15 seconds.
    /// </remarks>
    public TimeSpan RenewalInterval => LeaseDuration / 2;
}
