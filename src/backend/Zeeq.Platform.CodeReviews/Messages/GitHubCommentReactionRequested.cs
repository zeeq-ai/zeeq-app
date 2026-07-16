using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Queue message that asks Zeeq to add a lightweight reaction to a GitHub comment.
/// </summary>
/// <remarks>
/// Feedback webhooks publish this message after a user comments on a PR issue
/// thread or PR review thread. The reaction is intentionally separate from
/// review execution and comment rendering: it is small, best-effort work that
/// tells the user Zeeq saw their feedback while later workflow slices decide
/// whether that feedback should start a new review or update a rendered
/// comment.
/// </remarks>
[ConfigurePublisher<ImmediateMessage>("github.comment.reaction")]
public sealed class GitHubCommentReactionRequested : Event, ITenantMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public GitHubCommentReactionRequested()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <summary>Local repository mapping id.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>GitHub owner/name value, for example <c>zeeq-ai/zeeq</c>.</summary>
    public required string OwnerQualifiedRepoName { get; init; }

    /// <summary>Pull request number for trace and log context.</summary>
    public int PullRequestNumber { get; init; }

    /// <summary>Comment target that should receive the reaction.</summary>
    public required GitHubCommentReactionTarget Target { get; init; }

    /// <summary>GitHub reaction content, for example <c>+1</c>.</summary>
    public required string ReactionContent { get; init; }

    /// <summary>GitHub delivery id or upstream signal id that requested the reaction.</summary>
    public required string SignalId { get; init; }

    /// <summary>Trace context captured from the upstream webhook delivery.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}
