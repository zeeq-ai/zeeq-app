using System.Security.Claims;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Runs resolved reviewer agents and returns canonical review XML.
/// </summary>
/// <remarks>
/// The real implementation uses Agent Framework. The runner depends on this
/// small seam so tests can validate context assembly, artifact persistence, and
/// finding counts without constructing provider LLM clients.
/// </remarks>
public interface ICodeReviewAgentExecutor
{
    /// <summary>
    /// Executes the active reviewer set for one organization and prompt.
    /// </summary>
    /// <remarks>
    /// The <paramref name="telemetry" /> collector is threaded to every reviewer agent (via the
    /// telemetry middleware and per-run options) so the sources each reviewer consulted are
    /// captured during the run. The runner owns its lifecycle and reads its snapshot afterwards.
    /// </remarks>
    Task<string> ExecuteAsync(
        string organizationId,
        IReadOnlyList<CodeReviewerRuntimeAgent> activeReviewers,
        bool noAgentsActivated,
        CodeReviewUserPrompt codeReviewUserPrompt,
        IReadOnlyList<CodeReviewPreviousReview> previousReviews,
        ClaimsPrincipal callerIdentity,
        CodeReviewTelemetryContext telemetry,
        CancellationToken cancellationToken
    );
}
