using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runs the code review engine for a persisted review record.
/// </summary>
/// <remarks>
/// The runner receives the queue message as well as the durable review row
/// because exact partition-aware context such as the pull request created time
/// travels on the message, not on <see cref="CodeReviewRecord"/>.
/// </remarks>
public interface ICodeReviewRunner
{
    /// <summary>
    /// Runs review work for the supplied queue message and review record.
    /// </summary>
    Task<CodeReviewRunResult> RunAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Compact result returned by a code review runner.
/// </summary>
/// <param name="SourceTelemetryPayload">Serialized <c>CodeReviewSourceTelemetry</c> jsonb payload.</param>
/// <param name="FindingsStorageUri">Storage URI for the canonical review XML artifact.</param>
/// <param name="CriticalFindings">Number of critical findings.</param>
/// <param name="MajorFindings">Number of major findings.</param>
/// <param name="MinorFindings">Number of minor findings.</param>
/// <param name="SuggestionFindings">Number of suggestion findings.</param>
/// <param name="CommentFindings">Number of informational comment findings.</param>
public sealed record CodeReviewRunResult(
    string SourceTelemetryPayload,
    string FindingsStorageUri,
    int CriticalFindings,
    int MajorFindings,
    int MinorFindings,
    int SuggestionFindings,
    int CommentFindings
);

/// <summary>
/// Phase-one fake runner used until the real review engine is wired.
/// </summary>
/// <remarks>
/// The stub deliberately returns an empty successful result. That lets queue,
/// store, active-lock, and comment-signal behavior be tested now while keeping
/// final code analysis out of this slice.
/// </remarks>
public sealed class StubCodeReviewRunner : ICodeReviewRunner
{
    /// <inheritdoc />
    public Task<CodeReviewRunResult> RunAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult(
            new CodeReviewRunResult(
                CodeReviewRecord.EmptySourceTelemetryPayload,
                string.Empty,
                0,
                0,
                0,
                0,
                0
            )
        );
}
