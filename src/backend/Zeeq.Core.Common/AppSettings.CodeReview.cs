namespace Zeeq.Core.Common;

/// <summary>
/// The runtime application settings loaded from `appsettings.json` and the runtime environment.
/// </summary>
public sealed partial record AppSettings
{
    /// <summary>
    /// Automated code-review configuration.
    /// </summary>
    public CodeReviewSettings CodeReview { get; init; } = new();
}

/// <summary>
/// Settings that control automated code-review workflow policy.
/// </summary>
/// <remarks>
/// V2 keeps these settings provider-neutral because GitHub is only one way to
/// request and render reviews. The reference Zeeq application names the
/// budget <c>DefaultRereviewAllowance</c>; V2 stores the same policy value as
/// <see cref="DefaultReviewBudget"/> because the persisted model uses
/// <c>RemainingReviewBudget</c>.
/// </remarks>
public sealed record CodeReviewSettings
{
    /// <summary>
    /// Stable identity used by automated code-review agents.
    /// </summary>
    public string AgentIdentity { get; init; } = "zeeq-code-review-agent";

    /// <summary>
    /// Plain text key material used to derive the AES-GCM key for review request links.
    /// </summary>
    /// <remarks>
    /// Store the real value in user secrets or environment variables. The value
    /// is hashed before use; it is not expected to already be a raw AES key.
    /// </remarks>
    public string ReviewRequestLinkEncryptionKey { get; init; } = string.Empty;

    /// <summary>
    /// Number of days a rendered review request link remains valid.
    /// </summary>
    public int ReviewRequestLinkValidityDays { get; init; } = 7;

    /// <summary>
    /// Number of minutes an MCP uploaded-diff URL remains valid.
    /// </summary>
    public int DiffUploadUrlValidityMinutes { get; init; } = 30;

    /// <summary>
    /// Maximum raw diff upload size accepted by the MCP code-review upload endpoint.
    /// </summary>
    public int DiffUploadMaxBytes { get; init; } = 500_000;

    /// <summary>
    /// Number of follow-up review requests allowed after the initial review is accepted.
    /// </summary>
    /// <remarks>
    /// Initial reviews start with this value. Later slices that enqueue a
    /// re-review should decrement this count when the new review request is
    /// accepted, matching the reference implementation's allowance-spend point.
    /// </remarks>
    public int DefaultReviewBudget { get; init; } = 20;
}
