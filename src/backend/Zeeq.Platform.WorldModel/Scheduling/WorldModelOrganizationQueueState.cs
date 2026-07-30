using Danom;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Scheduler state for one organization inside one tier-and-bucket lane.
/// </summary>
/// <remarks>
/// This is the DRR flow state. <see cref="Deficit"/> is the accumulated budget the organization
/// can spend on target groups in this lane. <see cref="ActiveTargetCount"/> is store-owned queue
/// metadata used to suppress inactive organizations from future scans.
/// <see cref="LastVisitedAtUtc"/> advances when a lane tick considers this organization, even if
/// the organization only accrues deficit and leases no work.
/// </remarks>
public sealed record WorldModelOrganizationQueueState(
    string OrganizationId,
    WorldModelSchedulerLane Lane,
    int Deficit,
    int ActiveTargetCount,
    Option<DateTimeOffset> LastVisitedAtUtc
)
{
    /// <summary>
    /// Validates and creates organization scheduler state.
    /// </summary>
    /// <param name="organizationId">Organization whose target queue is being scheduled.</param>
    /// <param name="lane">Tier-and-bucket lane containing this organization's target work.</param>
    /// <param name="deficit">Current DRR budget available to spend on target work.</param>
    /// <param name="activeTargetCount">Number of target groups currently active for the organization.</param>
    /// <param name="lastVisitedAtUtc">UTC timestamp from the last lane tick that considered this organization.</param>
    public static Result<WorldModelOrganizationQueueState, string> Create(
        string organizationId,
        WorldModelSchedulerLane lane,
        int deficit,
        int activeTargetCount,
        Option<DateTimeOffset> lastVisitedAtUtc
    )
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Result<WorldModelOrganizationQueueState, string>.Error(
                "Organization id is required."
            );
        }

        if (deficit < 0)
        {
            return Result<WorldModelOrganizationQueueState, string>.Error(
                "Deficit must be zero or greater."
            );
        }

        if (activeTargetCount < 0)
        {
            return Result<WorldModelOrganizationQueueState, string>.Error(
                "Active target count must be zero or greater."
            );
        }

        return Result<WorldModelOrganizationQueueState, string>.Ok(
            new(organizationId.Trim(), lane, deficit, activeTargetCount, lastVisitedAtUtc)
        );
    }
}
