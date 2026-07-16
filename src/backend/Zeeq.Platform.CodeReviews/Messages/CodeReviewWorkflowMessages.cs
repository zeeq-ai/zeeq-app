using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Queue message that starts execution for a persisted code review record.
/// </summary>
/// <remarks>
/// The pull-request webhook handler creates the review row and active-review
/// guard before publishing this message. The runner handler can then load the
/// exact partitioned review row by <see cref="CodeReviewRecordId"/> and
/// <see cref="CodeReviewCreatedAtUtc"/> without scanning review partitions.
/// </remarks>
[ConfigurePublisher("code-review.run")]
public sealed class CodeReviewRunRequested : Event, ITenantMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public CodeReviewRunRequested()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <summary>Local repository mapping id.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>GitHub owner/name value, for example <c>zeeq-ai/zeeq</c>.</summary>
    public required string OwnerQualifiedRepoName { get; init; }

    /// <summary>GitHub pull request number.</summary>
    public int PullRequestNumber { get; init; }

    /// <summary>Partitioned pull request record id.</summary>
    public required string PullRequestRecordId { get; init; }

    /// <summary>Partition key for the pull request record.</summary>
    public DateTimeOffset PullRequestCreatedAtUtc { get; init; }

    /// <summary>Partitioned code review record id.</summary>
    public required string CodeReviewRecordId { get; init; }

    /// <summary>Partition key for the code review record.</summary>
    public DateTimeOffset CodeReviewCreatedAtUtc { get; init; }

    /// <summary>GitHub delivery id that accepted the review request.</summary>
    public required string GitHubDeliveryId { get; init; }

    /// <summary>Trace context captured at webhook ingress.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}

/// <summary>
/// Signals that a Zeeq-owned GitHub comment target needs to be rendered.
/// </summary>
/// <remarks>
/// This message is intentionally a wake-up signal, not rendered comment state.
/// Producers provide the logical target, render kind, optional clear markers,
/// and references to authoritative records such as <see cref="CodeReviewRecord" />.
/// The consumer acquires the target lease, reads the live GitHub comment body,
/// loads the referenced record, renders a full DOM-preserving body, and writes
/// that body back to GitHub.
///
/// The message uses the immediate queue lane for both first acknowledgements and
/// later completed-review updates. Immediate placeholder content is cheap and
/// later messages replace it in place through the same target-scoped pipeline.
/// </remarks>
[ConfigurePublisher<ImmediateMessage>("github.comment.write")]
public sealed class GitHubCommentWriteRequested : Event, ITenantMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public GitHubCommentWriteRequested()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <summary>Local repository mapping id.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>GitHub owner/name value, for example <c>zeeq-ai/zeeq</c>.</summary>
    public required string OwnerQualifiedRepoName { get; init; }

    /// <summary>GitHub pull request number.</summary>
    public int PullRequestNumber { get; init; }

    /// <summary>Logical GitHub comment target to serialize, resolve, render, and write.</summary>
    public required GitHubCommentTargetSelector Target { get; init; }

    /// <summary>Render kind that selects the renderer behavior.</summary>
    /// <remarks>
    /// Current values include <c>draft_prompt</c>, <c>closed</c>, <c>ignored</c>,
    /// <c>already_running</c>, <c>allowance_exhausted</c>, <c>queued</c>, and
    /// <c>stub_review_completed</c>. These remain strings so new render kinds can
    /// be introduced without forcing a queue schema migration.
    /// </remarks>
    public required string Kind { get; init; }

    /// <summary>Section markers that must be removed before renderer patches run.</summary>
    /// <remarks>
    /// This is the explicit escape hatch for stale sections. The consumer does
    /// not infer broad deletes from the render kind; it only clears the markers
    /// named here and then preserves all other existing DOM sections.
    /// </remarks>
    public IReadOnlyList<string> Clear { get; init; } = [];

    /// <summary>Optional partitioned code review record id used by review-backed renders.</summary>
    public string? CodeReviewRecordId { get; init; }

    /// <summary>Optional code review partition key paired with <see cref="CodeReviewRecordId" />.</summary>
    public DateTimeOffset? CodeReviewCreatedAtUtc { get; init; }

    /// <summary>GitHub delivery id or run signal that caused this durable write request.</summary>
    public required string SignalId { get; init; }

    /// <summary>Trace context captured from the upstream workflow.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}
