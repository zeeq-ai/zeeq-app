namespace Zeeq.Core.Models;

/// <summary>
/// Tracked GitHub check-run lifecycle state for one pull request.
/// </summary>
/// <remarks>
/// Stored as a nullable JSONB column <c>check_run_state</c> on the partitioned
/// <see cref="PullRequestRecord"/> table. A null value means Zeeq never posted a
/// check for this PR (either the repository does not have check-run gating
/// enabled, or the PR was skipped because it was a draft at the time of the
/// first review).
///
/// When the check is posted and not bypassed, <see cref="CheckRunId"/> holds
/// the GitHub check-run id that subsequent Update calls target. The state is
/// stored rather than recovered via <c>GetAllForReference</c> because any
/// missing-state scenario is a non-blocking edge case (the previous head-shas
/// are no longer being evaluated).
/// </remarks>
public sealed class PullRequestCheckRunState
{
    /// <summary>
    /// GitHub check-run id used for later Update (resolve/bypass) API calls.
    /// </summary>
    public required long CheckRunId { get; set; }

    /// <summary>
    /// Head commit SHA the check run is bound to.
    /// </summary>
    public required string HeadSha { get; set; }

    /// <summary>
    /// Whether the check currently gates the merge.
    /// </summary>
    public CheckRunBlockState State { get; set; }

    /// <summary>
    /// Who removed or cleared the block.
    /// </summary>
    /// <remarks>
    /// Possible values: a GitHub login (comment or native button bypass),
    /// a Zeeq user id prefixed with <c>zeeq-user:</c> (UI bypass), or
    /// <c>zeeq:auto-cleared</c> (follow-up clean review auto-resolved the
    /// block). Null while <see cref="State"/> is <see cref="CheckRunBlockState.Blocking"/>.
    /// </remarks>
    public string? RemovedBy { get; set; }

    /// <summary>
    /// When the block was removed. Null while <see cref="State"/> is
    /// <see cref="CheckRunBlockState.Blocking"/>.
    /// </summary>
    public DateTimeOffset? RemovedAtUtc { get; set; }
}

/// <summary>
/// Lifecycle state of a Zeeq check run on a pull request.
/// </summary>
public enum CheckRunBlockState
{
    /// <summary>
    /// The check run is active and may gate the merge.
    /// </summary>
    Blocking = 0,

    /// <summary>
    /// The block was cleared by a bypass or a follow-up clean review.
    /// </summary>
    Removed = 1,
}
