using Danom;
using Zeeq.Core.Models;

namespace Zeeq.Platform.WorldModel.Scheduling;

/// <summary>
/// Target-scoped unit of work selected by the world model scheduler.
/// </summary>
/// <remarks>
/// This is the atomic scheduling unit for the world model pipeline. It represents all pending
/// events for one <c>(organizationId, targetId)</c> group so Curator and Cluster Index Builder
/// workers can process related mutations together, dedupe them, and make a single coordinated
/// LLM or embedding pass when needed.
/// </remarks>
public sealed record WorldModelTargetWorkItem(
    string OrganizationId,
    string TargetId,
    OrganizationTier Tier,
    int Bucket,
    int EventCount,
    int EstimatedCost,
    DateTimeOffset OldestEventAtUtc,
    DateTimeOffset NewestEventAtUtc
)
{
    /// <summary>
    /// Validates and creates a target-scoped work item.
    /// </summary>
    /// <param name="organizationId">Organization that owns the target and pending events.</param>
    /// <param name="targetId">Identifier of the thing being mutated by the event group.</param>
    /// <param name="tier">Resolved organization tier used to choose the scheduler lane.</param>
    /// <param name="bucket">Stable route bucket within the tier.</param>
    /// <param name="eventCount">Number of raw events represented by this target group.</param>
    /// <param name="estimatedCost">Deficit cost to spend when the target group is leased.</param>
    /// <param name="oldestEventAtUtc">UTC timestamp of the oldest event in the group.</param>
    /// <param name="newestEventAtUtc">UTC timestamp of the newest event in the group.</param>
    public static Result<WorldModelTargetWorkItem, string> Create(
        string organizationId,
        string targetId,
        OrganizationTier tier,
        int bucket,
        int eventCount,
        int estimatedCost,
        DateTimeOffset oldestEventAtUtc,
        DateTimeOffset newestEventAtUtc
    )
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Result<WorldModelTargetWorkItem, string>.Error("Organization id is required.");
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            return Result<WorldModelTargetWorkItem, string>.Error("Target id is required.");
        }

        if (bucket < 0)
        {
            return Result<WorldModelTargetWorkItem, string>.Error(
                "Bucket must be zero or greater."
            );
        }

        if (eventCount < 1)
        {
            return Result<WorldModelTargetWorkItem, string>.Error(
                "Event count must be greater than zero."
            );
        }

        if (estimatedCost < 1)
        {
            return Result<WorldModelTargetWorkItem, string>.Error(
                "Estimated cost must be greater than zero."
            );
        }

        if (newestEventAtUtc < oldestEventAtUtc)
        {
            return Result<WorldModelTargetWorkItem, string>.Error(
                "Newest event time must be greater than or equal to oldest event time."
            );
        }

        return Result<WorldModelTargetWorkItem, string>.Ok(
            new(
                organizationId.Trim(),
                targetId.Trim(),
                tier,
                bucket,
                eventCount,
                estimatedCost,
                oldestEventAtUtc,
                newestEventAtUtc
            )
        );
    }
}
