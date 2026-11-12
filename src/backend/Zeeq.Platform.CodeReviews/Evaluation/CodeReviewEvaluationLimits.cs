namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Shared v1 limits for reviewer backtesting.
/// </summary>
/// <remarks>
/// Advertised limits, server-side validation and the historical admission behaviour must
/// agree, so every layer reads these constants instead of restating numbers. These are hard
/// limits, not configurable quotas, and they deliberately do not bound retained history.
/// </remarks>
public static class CodeReviewEvaluationLimits
{
    /// <summary>Maximum live cases in one dataset and in one run manifest.</summary>
    public const int MaxCases = 25;

    /// <summary>Maximum concurrent logical case pipelines per admitted organization run.</summary>
    public const int MaxConcurrentCases = 2;

    /// <summary>Maximum serialized UTF-8 snapshot size per case.</summary>
    public const int MaxSnapshotBytes = 5 * 1024 * 1024;

    /// <summary>Maximum reference findings per reviewer and case.</summary>
    public const int MaxReferenceFindings = 100;

    /// <summary>Maximum reference finding title length.</summary>
    public const int MaxReferenceTitleLength = 300;

    /// <summary>Maximum reference finding summary length.</summary>
    public const int MaxReferenceSummaryLength = 4000;

    /// <summary>Maximum dataset name length.</summary>
    public const int MaxDatasetNameLength = 256;

    /// <summary>Maximum dataset description length.</summary>
    public const int MaxDatasetDescriptionLength = 2000;

    /// <summary>Lifetime of one staged capture token.</summary>
    public static readonly TimeSpan CaptureTokenLifetime = TimeSpan.FromMinutes(15);
}
