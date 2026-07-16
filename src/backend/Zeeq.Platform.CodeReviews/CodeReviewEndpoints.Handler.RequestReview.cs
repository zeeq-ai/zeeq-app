using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles manual review requests for one selected pull request.
/// </summary>
public sealed class RequestCodeReviewHandler(
    CodeReviewAuthorization authorization,
    IPullRequestRecordStore pullRequests,
    ICodeRepositoryStore repositories,
    CodeReviewRequestService reviewRequests
) : IEndpointHandler
{
    /// <summary>
    /// Requests a new review through the same durable workflow path used by webhooks.
    /// </summary>
    /// <remarks>
    /// Manual requests target an existing partitioned pull request row. They
    /// bypass the draft and webhook-action gates because the user explicitly
    /// asked for the review, but they still enforce repository visibility,
    /// closed-PR, budget, and active-review guards inside
    /// <see cref="CodeReviewRequestService"/>.
    /// </remarks>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewManualRequestResponse>>
    > HandleAsync(
        string organizationId,
        string pullRequestRecordId,
        DateTimeOffset? createdAtUtc,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("missing_organization", "Organization id is required.")
            );
        }

        if (createdAtUtc is null)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "missing_created_at",
                    "createdAtUtc is required for partition-aware manual review requests."
                )
            );
        }

        if (await authorization.ResolveAsync(organizationId, user, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var pullRequest = await pullRequests.FindAsync(
            pullRequestRecordId,
            createdAtUtc.Value,
            cancellationToken
        );
        if (pullRequest is null || pullRequest.OrganizationId != organizationId)
        {
            return TypedResults.NotFound();
        }

        if (!await IsRepositoryActionableAsync(pullRequest, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var result = await reviewRequests.RequestAsync(
            CreateRequest(pullRequest),
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewManualRequestResponse(
                result.Outcome,
                result.CommentKind,
                CodeReviewEndpointMapping.ToDto(result.PullRequest),
                result.CodeReview is null
                    ? null
                    : CodeReviewEndpointMapping.ToDto(result.CodeReview)
            )
        );
    }

    private async Task<bool> IsRepositoryActionableAsync(
        PullRequestRecord pullRequest,
        CancellationToken cancellationToken
    )
    {
        var repository = await repositories.FindActiveForOrganizationAsync(
            pullRequest.OrganizationId,
            pullRequest.RepositoryId,
            cancellationToken
        );

        return repository is not null
            && (
                repository.TeamId is null
                || string.Equals(repository.TeamId, pullRequest.TeamId, StringComparison.Ordinal)
            );
    }

    private static CodeReviewRequest CreateRequest(PullRequestRecord pullRequest)
    {
        var signalId = $"manual_{Guid.CreateVersion7():N}";

        return new(
            OrganizationId: pullRequest.OrganizationId,
            TeamId: pullRequest.TeamId,
            RepositoryId: pullRequest.RepositoryId,
            OwnerQualifiedRepoName: pullRequest.OwnerQualifiedRepoName,
            PullRequestNumber: pullRequest.PullRequestNumber,
            PullRequestNodeId: pullRequest.GitHubNodeId,
            Title: pullRequest.Title,
            HeadRef: pullRequest.Branch,
            BaseRef: pullRequest.BaseBranch,
            HeadSha: pullRequest.HeadSha,
            AuthorLogin: pullRequest.AuthorLogin,
            HtmlUrl: pullRequest.HtmlUrl,
            IsDraft: pullRequest.IsDraft,
            State: pullRequest.State.ToString(),
            TriggerAction: "manual",
            RequestOrigin: CodeReviewRequestOrigin.Manual,
            SignalId: signalId,
            RunRequestGitHubDeliveryId: signalId,
            TraceContext: ZeeqTelemetry.CaptureCurrentTraceContext(),
            BypassDraftGate: true,
            BypassTriggerActionGate: true,
            BypassStateGate: true,
            PullRequestRecordId: pullRequest.Id,
            PullRequestCreatedAtUtc: pullRequest.CreatedAtUtc,
            PullRequestCreatedFromWebhookAtUtc: pullRequest.CreatedFromWebhookAtUtc,
            PullRequestLastWebhookAtUtc: pullRequest.LastWebhookAtUtc,
            PullRequestClaimStatus: pullRequest.ClaimStatus,
            PullRequestClaimedByUserId: pullRequest.ClaimedByUserId,
            PullRequestFeatureId: pullRequest.FeatureId,
            PullRequestTagsJson: pullRequest.TagsJson,
            PullRequestLabelsJson: pullRequest.LabelsJson
        );
    }
}
