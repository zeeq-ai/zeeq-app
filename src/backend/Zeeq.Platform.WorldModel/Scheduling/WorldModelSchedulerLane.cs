using Danom;
using Zeeq.Core.Models;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Scheduler lane corresponding to one organization tier and route bucket.
/// </summary>
/// <remarks>
/// A lane maps to the existing tenant routing shape, such as <c>priority.00</c> or
/// <c>default.03</c>. Each scheduler call processes exactly one lane. Because an organization
/// belongs to one tier, its world-model target work should appear only in the lane for that tier
/// and its stable tenant bucket.
/// </remarks>
public sealed record WorldModelSchedulerLane(OrganizationTier Tier, int Bucket)
{
    /// <summary>
    /// Validates and creates a scheduler lane.
    /// </summary>
    /// <param name="tier">Resolved organization tier for the lane.</param>
    /// <param name="bucket">Stable route bucket within the tier.</param>
    public static Result<WorldModelSchedulerLane, string> Create(OrganizationTier tier, int bucket)
    {
        if (bucket < 0)
        {
            return Result<WorldModelSchedulerLane, string>.Error("Bucket must be zero or greater.");
        }

        return Result<WorldModelSchedulerLane, string>.Ok(new(tier, bucket));
    }
}
