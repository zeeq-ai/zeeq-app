namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Provider-neutral interface for creating and updating GitHub check runs.
/// </summary>
/// <remarks>
/// The interface lives in the platform layer so domain services can depend on it
/// without coupling to Octokit. <see cref="CheckRunWrite"/> uses Zeeq enums
/// (<see cref="CheckRunStatusKind"/>, <see cref="CheckRunConclusionKind"/>) to
/// keep the Octokit dependency inside the GitHub integration assembly.
/// </remarks>
public interface ICheckRunClient
{
    /// <summary>
    /// Creates a check run on a repository's head commit and returns the GitHub check-run id.
    /// </summary>
    /// <param name="organizationId">Zeeq organization id.</param>
    /// <param name="ownerQualifiedRepoName">Owner-qualified repository name, for example <c>zeeq-ai/zeeq</c>.</param>
    /// <param name="write">Check-run details to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The GitHub check-run id to use for later Update calls.</returns>
    Task<long> CreateAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        CheckRunWrite write,
        CancellationToken ct
    );

    /// <summary>
    /// Updates an existing check run by its GitHub id.
    /// </summary>
    /// <param name="organizationId">Zeeq organization id.</param>
    /// <param name="ownerQualifiedRepoName">Owner-qualified repository name.</param>
    /// <param name="checkRunId">GitHub check-run id returned from <see cref="CreateAsync"/>.</param>
    /// <param name="write">Check-run details to update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        long checkRunId,
        CheckRunWrite write,
        CancellationToken ct
    );
}

/// <summary>
/// Provider-neutral check-run write payload used by <see cref="ICheckRunClient"/>.
/// </summary>
/// <param name="Name">Published check-run context name.</param>
/// <param name="HeadSha">Commit SHA the check run is bound to. Only used during creation.</param>
/// <param name="Status">Current check status.</param>
/// <param name="Conclusion">Check conclusion, required when <paramref name="Status"/> is <see cref="CheckRunStatusKind.Completed"/>.</param>
/// <param name="Title">Short title for the check output summary.</param>
/// <param name="Summary">Markdown summary for the check output.</param>
/// <param name="DetailsUrl">Optional deep-link URL shown in the GitHub UI.</param>
/// <param name="IncludeBypassAction">When true, attach a Bypass action button to the check run.</param>
public sealed record CheckRunWrite(
    string Name,
    string HeadSha,
    CheckRunStatusKind Status,
    CheckRunConclusionKind? Conclusion,
    string Title,
    string Summary,
    string? DetailsUrl,
    bool IncludeBypassAction
);

/// <summary>
/// GitHub check-run status as defined by the Checks API.
/// </summary>
public enum CheckRunStatusKind
{
    /// <summary>
    /// Equivalent to Octokit <c>CheckStatus.InProgress</c>.
    /// </summary>
    InProgress = 0,

    /// <summary>
    /// Equivalent to Octokit <c>CheckStatus.Completed</c>.
    /// </summary>
    Completed = 1,
}

/// <summary>
/// GitHub check-run conclusion as defined by the Checks API.
/// </summary>
/// <remarks>
/// Only the conclusions Zeeq writes are modelled here.
/// </remarks>
public enum CheckRunConclusionKind
{
    /// <summary>
    /// Below-threshold or all-clear review.
    /// </summary>
    Success = 0,

    /// <summary>
    /// No reviewable content or no agents activated.
    /// </summary>
    Neutral = 1,

    /// <summary>
    /// Findings meet or exceed the configured blocking threshold.
    /// </summary>
    ActionRequired = 2,
}
