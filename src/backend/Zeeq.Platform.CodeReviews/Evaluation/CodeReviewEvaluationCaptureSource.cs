using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider-neutral source for evaluation pull request capture.
/// </summary>
/// <remarks>
/// This is separate from <see cref="ICodeReviewPullRequestSource"/> because evaluation capture
/// needs base/head identities, capture-time stability checks and the provider's declared
/// changed-file count, and because it must not depend on a persisted production review
/// request. Implementations never create PR, review, comment or check-run rows.
/// </remarks>
public interface ICodeReviewEvaluationPullRequestCaptureSource
{
    /// <summary>Captures one pull request's reviewable inputs.</summary>
    Task<CodeReviewEvaluationSnapshot> CaptureAsync(
        CodeReviewEvaluationCaptureRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Marker for the real, provider-backed evaluation capture source.
/// </summary>
/// <remarks>
/// Development fixture decorators depend on this marker rather than on the base interface, so
/// they can wrap the real provider source without a circular dependency. Production registers
/// only the marker implementation as <see cref="ICodeReviewEvaluationPullRequestCaptureSource"/>.
/// </remarks>
public interface ICodeReviewEvaluationProviderCaptureSource
    : ICodeReviewEvaluationPullRequestCaptureSource;

/// <summary>
/// Input for one evaluation capture.
/// </summary>
/// <param name="OrganizationId">Organization that owns the capture.</param>
/// <param name="RepositoryId">Repository mapping the capture belongs to.</param>
/// <param name="OwnerQualifiedRepoName">Provider-qualified repository name.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
public sealed record CodeReviewEvaluationCaptureRequest(
    string OrganizationId,
    string RepositoryId,
    string OwnerQualifiedRepoName,
    int PullRequestNumber
);

/// <summary>
/// A capture that cannot be used as a trustworthy evaluation input.
/// </summary>
/// <remarks>
/// Codes map directly to API error codes: <c>pr_not_found</c>, <c>pr_no_access</c>,
/// <c>pull_request_changed</c>, <c>snapshot_incomplete</c> or <c>snapshot_too_large</c>.
/// </remarks>
public sealed class CodeReviewEvaluationCaptureException : Exception
{
    /// <summary>Creates a capture failure with a stable error code.</summary>
    public CodeReviewEvaluationCaptureException(string code, string message)
        : base(message) => Code = code;

    /// <summary>Stable machine-readable failure code.</summary>
    public string Code { get; }
}

/// <summary>
/// Resolved, staged capture plus its signed token.
/// </summary>
/// <param name="Capture">Staged capture to install on save.</param>
/// <param name="PullRequestTitle">Captured title for preview rendering.</param>
/// <param name="CaptureToken">Opaque signed token to echo back on save.</param>
/// <param name="ExpiresAtUtc">Token expiry.</param>
public sealed record CodeReviewEvaluationCaptureOutcome(
    CodeReviewEvaluationStagedCapture Capture,
    string PullRequestTitle,
    string CaptureToken,
    DateTimeOffset ExpiresAtUtc
);
