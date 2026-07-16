using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Common tenant and GitHub delivery metadata carried by GitHub webhook messages.
/// </summary>
/// <remarks>
/// Webhook ingress resolves a GitHub repository to a Zeeq repository mapping
/// before publishing any of these messages. The shared metadata lets downstream
/// handlers make idempotent decisions from the GitHub delivery id, continue the
/// ingress trace, and avoid calling GitHub just to recover tenant context.
/// </remarks>
public interface IGitHubWebhookTenantMessage : ITenantMessage
{
    /// <summary>Zeeq repository mapping that accepted this delivery.</summary>
    string RepositoryId { get; }

    /// <summary>GitHub owner/name value, for example <c>zeeq-ai/zeeq</c>.</summary>
    string OwnerQualifiedRepoName { get; }

    /// <summary>GitHub delivery id from <c>X-GitHub-Delivery</c>.</summary>
    string GitHubDeliveryId { get; }

    /// <summary>GitHub event name from <c>X-GitHub-Event</c>.</summary>
    string GitHubEvent { get; }

    /// <summary>GitHub event action such as <c>opened</c> or <c>created</c>.</summary>
    string GitHubAction { get; }

    /// <summary>GitHub App installation id from the webhook payload.</summary>
    long GitHubInstallationId { get; }

    /// <summary>Trace context captured at webhook ingress.</summary>
    ZeeqTraceContext TraceContext { get; }
}

/// <summary>
/// Queue message for a GitHub pull-request lifecycle webhook.
/// </summary>
/// <remarks>
/// This is the durable handoff between anonymous GitHub webhook ingress and the
/// code-review workflow. Handlers in the next slice will claim the delivery,
/// upsert <see cref="PullRequestRecord"/>, enforce review gates, and publish
/// immediate comment/review-run work.
/// </remarks>
[ConfigurePublisher<PriorityMessage>("github.webhook.pull-request")]
public sealed class GitHubPullRequestWebhookReceived : Event, IGitHubWebhookTenantMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public GitHubPullRequestWebhookReceived()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <inheritdoc />
    public required string RepositoryId { get; init; }

    /// <inheritdoc />
    public required string OwnerQualifiedRepoName { get; init; }

    /// <inheritdoc />
    public required string GitHubDeliveryId { get; init; }

    /// <inheritdoc />
    public required string GitHubEvent { get; init; }

    /// <inheritdoc />
    public required string GitHubAction { get; init; }

    /// <inheritdoc />
    public long GitHubInstallationId { get; init; }

    /// <inheritdoc />
    public required ZeeqTraceContext TraceContext { get; init; }

    /// <summary>GitHub pull request number inside the repository.</summary>
    public long PullRequestNumber { get; init; }

    /// <summary>GitHub GraphQL node id for the pull request.</summary>
    public string? PullRequestNodeId { get; init; }

    /// <summary>Human-readable PR title at the time of delivery.</summary>
    public string? Title { get; init; }

    /// <summary>Browser URL for the PR.</summary>
    public string? HtmlUrl { get; init; }

    /// <summary>True when the PR is still a draft.</summary>
    public bool IsDraft { get; init; }

    /// <summary>GitHub PR state value, for example <c>open</c> or <c>closed</c>.</summary>
    public string? State { get; init; }

    /// <summary>Current head branch ref.</summary>
    public string? HeadRef { get; init; }

    /// <summary>Current base branch ref.</summary>
    public string? BaseRef { get; init; }

    /// <summary>Current head commit SHA.</summary>
    public string? HeadSha { get; init; }

    /// <summary>GitHub login for the PR author.</summary>
    public string? AuthorLogin { get; init; }
}

/// <summary>
/// Queue message for a GitHub PR issue-comment webhook.
/// </summary>
/// <remarks>
/// GitHub sends whole-PR conversation comments as issue comments. The webhook
/// adapter only publishes this message when the issue payload is for a pull
/// request and the comment body starts with a supported Zeeq command; plain
/// issue comments and normal PR discussion are acknowledged as no-op at ingress.
/// </remarks>
[ConfigurePublisher<PriorityMessage>("github.webhook.issue-comment")]
public sealed class GitHubIssueCommentWebhookReceived : Event, IGitHubWebhookTenantMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public GitHubIssueCommentWebhookReceived()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <inheritdoc />
    public required string RepositoryId { get; init; }

    /// <inheritdoc />
    public required string OwnerQualifiedRepoName { get; init; }

    /// <inheritdoc />
    public required string GitHubDeliveryId { get; init; }

    /// <inheritdoc />
    public required string GitHubEvent { get; init; }

    /// <inheritdoc />
    public required string GitHubAction { get; init; }

    /// <inheritdoc />
    public long GitHubInstallationId { get; init; }

    /// <inheritdoc />
    public required ZeeqTraceContext TraceContext { get; init; }

    /// <summary>PR issue number, which is also the pull request number.</summary>
    public long PullRequestNumber { get; init; }

    /// <summary>GitHub issue node id associated with the PR conversation.</summary>
    public string? IssueNodeId { get; init; }

    /// <summary>GitHub issue-comment id.</summary>
    public long CommentId { get; init; }

    /// <summary>GitHub issue-comment node id.</summary>
    public string? CommentNodeId { get; init; }

    /// <summary>Raw command comment body for later feedback-command execution.</summary>
    public string? CommentBody { get; init; }

    /// <summary>GitHub login for the comment author.</summary>
    public string? CommentAuthorLogin { get; init; }

    /// <summary>Browser URL for the comment.</summary>
    public string? CommentHtmlUrl { get; init; }
}

/// <summary>
/// Queue message for a GitHub pull-request review-comment webhook.
/// </summary>
/// <remarks>
/// Diff comments are the second feedback-command surface. The webhook adapter
/// only publishes this message for supported Zeeq command comments. Downstream
/// handlers can enqueue immediate reaction work and publish review/comment
/// updates without doing any of that inside the webhook request.
/// </remarks>
[ConfigurePublisher<PriorityMessage>("github.webhook.review-comment")]
public sealed class GitHubPullRequestReviewCommentWebhookReceived
    : Event,
        IGitHubWebhookTenantMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public GitHubPullRequestReviewCommentWebhookReceived()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <inheritdoc />
    public required string RepositoryId { get; init; }

    /// <inheritdoc />
    public required string OwnerQualifiedRepoName { get; init; }

    /// <inheritdoc />
    public required string GitHubDeliveryId { get; init; }

    /// <inheritdoc />
    public required string GitHubEvent { get; init; }

    /// <inheritdoc />
    public required string GitHubAction { get; init; }

    /// <inheritdoc />
    public long GitHubInstallationId { get; init; }

    /// <inheritdoc />
    public required ZeeqTraceContext TraceContext { get; init; }

    /// <summary>Pull request number for the diff comment.</summary>
    public long PullRequestNumber { get; init; }

    /// <summary>GitHub PR node id for the diff comment target.</summary>
    public string? PullRequestNodeId { get; init; }

    /// <summary>GitHub review-comment id.</summary>
    public long CommentId { get; init; }

    /// <summary>GitHub review-comment node id.</summary>
    public string? CommentNodeId { get; init; }

    /// <summary>Raw diff command comment body for later feedback-command execution.</summary>
    public string? CommentBody { get; init; }

    /// <summary>GitHub login for the comment author.</summary>
    public string? CommentAuthorLogin { get; init; }

    /// <summary>Browser URL for the review comment.</summary>
    public string? CommentHtmlUrl { get; init; }

    /// <summary>Repository path targeted by the diff comment.</summary>
    public string? Path { get; init; }

    /// <summary>Commit SHA associated with the diff comment.</summary>
    public string? CommitId { get; init; }

    /// <summary>GitHub PR review id when the comment belongs to a review.</summary>
    public long PullRequestReviewId { get; init; }

    /// <summary>Parent comment id when this is a threaded reply.</summary>
    public long? InReplyToId { get; init; }
}
