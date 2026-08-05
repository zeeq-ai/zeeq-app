using Danom;
using Zeeq.Core.Models;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Tier weights used by the world model deficit round-robin scheduler.
/// </summary>
/// <remarks>
/// The quantum values encode service-tier throughput. A priority organization receives more
/// deficit per lane tick than a default organization, and a low-tier organization receives less.
/// Because deficit carries forward, lower tiers still make progress once enough budget accrues.
/// </remarks>
public sealed record WorldModelTierSchedulePolicy
{
    private WorldModelTierSchedulePolicy(
        int priorityQuantum,
        int defaultQuantum,
        int lowQuantum,
        int organizationScanMultiplier
    )
    {
        PriorityQuantum = priorityQuantum;
        DefaultQuantum = defaultQuantum;
        LowQuantum = lowQuantum;
        OrganizationScanMultiplier = organizationScanMultiplier;
    }

    /// <summary>
    /// Deficit quantum for priority-tier organizations.
    /// </summary>
    public int PriorityQuantum { get; }

    /// <summary>
    /// Deficit quantum for default-tier organizations.
    /// </summary>
    public int DefaultQuantum { get; }

    /// <summary>
    /// Deficit quantum for low-tier organizations.
    /// </summary>
    public int LowQuantum { get; }

    /// <summary>
    /// Number of organizations to scan per requested work item.
    /// </summary>
    public int OrganizationScanMultiplier { get; }

    /// <summary>
    /// Validates and creates a tier scheduling policy.
    /// </summary>
    /// <param name="priorityQuantum">Deficit added to priority-tier organizations per lane tick.</param>
    /// <param name="defaultQuantum">Deficit added to default-tier organizations per lane tick.</param>
    /// <param name="lowQuantum">Deficit added to low-tier organizations per lane tick.</param>
    /// <param name="organizationScanMultiplier">Multiplier that converts requested leases into organization scan breadth.</param>
    public static Result<WorldModelTierSchedulePolicy, string> Create(
        int priorityQuantum = 12,
        int defaultQuantum = 6,
        int lowQuantum = 2,
        int organizationScanMultiplier = 4
    )
    {
        if (
            priorityQuantum < 1
            || defaultQuantum < 1
            || lowQuantum < 1
            || organizationScanMultiplier < 1
        )
        {
            return Result<WorldModelTierSchedulePolicy, string>.Error(
                "Scheduling policy values must be greater than zero."
            );
        }

        return Result<WorldModelTierSchedulePolicy, string>.Ok(
            new(priorityQuantum, defaultQuantum, lowQuantum, organizationScanMultiplier)
        );
    }

    /// <summary>
    /// Returns the deficit quantum added to an active organization each scheduling pass.
    /// </summary>
    /// <remarks>
    /// The tier comes from the scheduler lane. Every organization returned for that lane should
    /// already belong to the same resolved tier; this method centralizes the tier-to-budget mapping.
    /// </remarks>
    /// <param name="tier">Resolved organization tier whose refill quantum should be returned.</param>
    public Result<int, string> GetQuantum(OrganizationTier tier) =>
        tier switch
        {
            OrganizationTier.Priority => Result<int, string>.Ok(PriorityQuantum),
            OrganizationTier.Default => Result<int, string>.Ok(DefaultQuantum),
            OrganizationTier.Low => Result<int, string>.Ok(LowQuantum),
            _ => Result<int, string>.Error("Unknown organization tier."),
        };
}
