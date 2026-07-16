namespace Zeeq.Core.Models;

/// <summary>
/// Organization-scoped operational configuration for code-review execution.
/// </summary>
/// <remarks>
/// This is persisted as a nullable typed JSON document on the organization row.
/// A missing document means the runtime should use <see cref="Default"/>.
/// </remarks>
public sealed record CodeReviewOrganizationSettings
{
    /// <summary>
    /// Default execution configuration used when an organization has not customized code reviews.
    /// </summary>
    public static CodeReviewOrganizationSettings Default => new();

    /// <summary>
    /// Maximum number of reviews that may execute concurrently for this organization.
    /// </summary>
    public int MaxConcurrentReviews { get; init; } = 4;

    /// <summary>
    /// Duration before an execution lease is considered abandoned.
    /// </summary>
    public TimeSpan ExecutionLeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
}
