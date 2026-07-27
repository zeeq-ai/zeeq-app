namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Controls non-persistence side effects during a code-review execution.
/// </summary>
/// <remarks>
/// Durable webhook reviews need the full production behavior: diagnostics, metrics,
/// previous-review context. Draft agent test runs need the same prompt construction
/// and reviewer execution, but they must not leak into durable review telemetry.
/// Artifact persistence is intentionally owned by <see cref="CodeReviewRunner"/> instead
/// of this options object so the execution engine can stay write-free.
/// </remarks>
public sealed record CodeReviewExecutionOptions(
    CodeReviewExecutionMode Mode,
    bool EmitDiagnostics,
    bool RecordMetrics,
    bool IncludePreviousReviews
)
{
    /// <summary>
    /// Production options for queued code-review runs.
    /// </summary>
    public static CodeReviewExecutionOptions Durable { get; } =
        new(
            CodeReviewExecutionMode.Durable,
            EmitDiagnostics: true,
            RecordMetrics: true,
            IncludePreviousReviews: true
        );

    /// <summary>
    /// Side-effect-limited options for draft test runs.
    /// </summary>
    public static CodeReviewExecutionOptions Test { get; } =
        new(
            CodeReviewExecutionMode.Test,
            EmitDiagnostics: false,
            RecordMetrics: false,
            IncludePreviousReviews: false
        );
}

/// <summary>
/// Identifies the caller-visible execution mode for a code-review run.
/// </summary>
public enum CodeReviewExecutionMode
{
    /// <summary>
    /// A normal persisted review run.
    /// </summary>
    Durable = 0,

    /// <summary>
    /// An ephemeral draft-agent test run.
    /// </summary>
    Test = 1,
}
