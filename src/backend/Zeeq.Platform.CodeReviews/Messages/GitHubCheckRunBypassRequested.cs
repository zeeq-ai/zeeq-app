using Zeeq.Core.Common;
using Zeeq.Platform.Messaging;
using Paramore.Brighter;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Queue message for a GitHub feedback-command bypass-check request.
/// </summary>
/// <remarks>
/// Published from the feedback-command webhook adapter when a comment body starts
/// with a supported Zeeq command token and the trailing text matches
/// <c>bypass check</c> or <c>bypass</c>. The handler calls
/// <see cref="ICheckRunService.BypassAsync"/> with the commenter's GitHub login
/// as <c>RemovedBy</c>. The +1 acknowledgement reaction still fires separately
/// from the existing reaction handler.
/// </remarks>
[ConfigurePublisher<PriorityMessage>("github.check_run.bypass_requested")]
public sealed class GitHubCheckRunBypassRequested : Event, ITenantMessage
{
    /// <summary>Creates the Brighter event with a generated message id.</summary>
    public GitHubCheckRunBypassRequested()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <summary>Zeeq repository id for the PR.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Provider-qualified repository name.</summary>
    public required string OwnerQualifiedRepoName { get; init; }

    /// <summary>Pull request number inside the repository.</summary>
    public int PullRequestNumber { get; init; }

    /// <summary>GitHub login of the commenter who requested the bypass.</summary>
    public required string CommentAuthorLogin { get; init; }

    /// <summary>GitHub delivery id for idempotency.</summary>
    public required string GitHubDeliveryId { get; init; }

    /// <summary>Trace context captured at webhook ingress.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}
