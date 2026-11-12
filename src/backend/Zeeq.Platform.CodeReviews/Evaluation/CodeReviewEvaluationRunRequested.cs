using Paramore.Brighter;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Durable wake-up signal for an admitted evaluation run.</summary>
[ConfigurePublisher<LowPriorityMessage>("code-review.evaluation.run")]
public sealed class CodeReviewEvaluationRunRequested : Event, ITenantMessage
{
    /// <summary>Creates a new queue signal.</summary>
    public CodeReviewEvaluationRunRequested()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <summary>Run to claim.</summary>
    public required string RunId { get; init; }
}
