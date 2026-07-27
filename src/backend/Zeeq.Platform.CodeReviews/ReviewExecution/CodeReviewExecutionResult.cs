using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider-neutral output from one code-review execution before durable storage.
/// </summary>
/// <remarks>
/// This is the reusable result shared by the queued durable runner and the draft
/// test-run proof path. It intentionally includes the validated XML and parsed
/// output so callers can either persist the artifact or return the findings directly.
/// </remarks>
public sealed record CodeReviewExecutionResult(
    string Xml,
    CodeReviewOutputDocument Output,
    string SourceTelemetryPayload,
    CodeReviewFindingCounts Counts,
    int InScopeFileCount,
    int OutOfScopeFileCount,
    int ReviewerCount,
    bool HasConfiguredAgents,
    bool NoAgentsActivated
);
