using System.Text;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runs one queued durable code-review attempt end to end.
/// </summary>
/// <remarks>
/// The provider-neutral execution core lives in <see cref="CodeReviewExecutionEngine"/>
/// so draft test runs can execute the same reviewer flow without durable writes. This
/// wrapper owns only the queued-run artifact persistence contract expected by
/// <see cref="CodeReviewRunRequestedHandler"/>.
/// </remarks>
public sealed partial class CodeReviewRunner(
    CodeReviewExecutionEngine executionEngine,
    ICodeReviewArtifactStore artifacts,
    ILogger<CodeReviewRunner> logger
) : ICodeReviewRunner
{
    private const string FindingsContentType = "application/xml";

    /// <inheritdoc />
    public async Task<CodeReviewRunResult> RunAsync(
        CodeReviewRunRequested message,
        CodeReviewRecord review,
        CancellationToken cancellationToken
    )
    {
        var execution = await executionEngine.RunAsync(
            message,
            review,
            CodeReviewExecutionOptions.Durable,
            runtimeAgentsOverride: null,
            cancellationToken
        );

        // Preserve the source telemetry as soon as execution succeeds. If artifact storage
        // throws below, CodeReviewRunRequestedHandler still persists this value on the errored
        // review row instead of losing the KB/tool trace captured during reviewer execution.
        review.SourceTelemetryPayload = execution.SourceTelemetryPayload;

        var findingsStorageUri = await WriteFindingsAsync(review, execution.Xml, cancellationToken);

        ZeeqTelemetry.AddEvent(
            [
                ("code_review.findings_storage_uri", findingsStorageUri),
                ("code_review.findings.critical", execution.Counts.Critical),
                ("code_review.findings.major", execution.Counts.Major),
                ("code_review.findings.minor", execution.Counts.Minor),
                ("code_review.findings.suggestion", execution.Counts.Suggestion),
                ("code_review.findings.comment", execution.Counts.Comment),
            ],
            "code_review.findings_artifact_written"
        );

        LogFindingsArtifactWritten(logger, review.Id, findingsStorageUri);

        return new(
            SourceTelemetryPayload: execution.SourceTelemetryPayload,
            FindingsStorageUri: findingsStorageUri,
            CriticalFindings: execution.Counts.Critical,
            MajorFindings: execution.Counts.Major,
            MinorFindings: execution.Counts.Minor,
            SuggestionFindings: execution.Counts.Suggestion,
            CommentFindings: execution.Counts.Comment
        );
    }

    private async Task<string> WriteFindingsAsync(
        CodeReviewRecord review,
        string xml,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        return await artifacts.WriteFindingsAsync(
            review,
            stream,
            FindingsContentType,
            cancellationToken
        );
    }

    [LoggerMessage(
        EventId = 3261,
        Level = LogLevel.Debug,
        Message = "Wrote code-review findings artifact. CodeReviewId={CodeReviewId}, FindingsStorageUri={FindingsStorageUri}"
    )]
    private static partial void LogFindingsArtifactWritten(
        ILogger logger,
        string codeReviewId,
        string findingsStorageUri
    );
}
