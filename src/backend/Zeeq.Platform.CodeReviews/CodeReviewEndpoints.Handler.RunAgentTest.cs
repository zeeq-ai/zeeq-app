using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles ephemeral reviewer-agent test runs from the Manage Agents editor.
/// </summary>
public sealed class RunCodeReviewAgentTestHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories,
    IPullRequestRecordStore pullRequests,
    CodeReviewExecutionEngine executionEngine
) : IEndpointHandler
{
    /// <summary>
    /// Runs one unsaved draft reviewer agent against a selected PR target.
    /// </summary>
    /// <remarks>
    /// This handler deliberately stops at the execution engine. It does not create durable review
    /// records, write findings artifacts, publish GitHub comments, update check runs, acquire active
    /// review locks, or decrement review budget.
    /// </remarks>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewAgentTestRunResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
        RunCodeReviewAgentTestRequest? request,
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

        var access = await authorization.ResolveAsync(organizationId, user, cancellationToken);
        if (access is null)
        {
            return TypedResults.NotFound();
        }

        if (!access.CanManage)
        {
            return TypedResults.Forbid();
        }

        if (request is null)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_agent_configuration",
                    "Request body is required."
                )
            );
        }

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        var repository = await repositories.FindActiveForOrganizationAsync(
            organizationId,
            repositoryId,
            cancellationToken
        );
        if (repository is null)
        {
            return TypedResults.NotFound();
        }

        var pullRequest = await pullRequests.FindAsync(
            request.PullRequestRecordId,
            request.PullRequestCreatedAtUtc,
            cancellationToken
        );
        if (
            pullRequest is null
            || pullRequest.OrganizationId != organizationId
            || pullRequest.RepositoryId != repository.Id
            || (
                repository.TeamId is not null
                && !string.Equals(pullRequest.TeamId, repository.TeamId, StringComparison.Ordinal)
            )
        )
        {
            return TypedResults.NotFound();
        }

        var review = CreateSyntheticReview(pullRequest);
        var message = CreateSyntheticMessage(pullRequest, review);
        var runtimeAgent = ToRuntimeAgent(request.Agent);

        var result = await executionEngine.RunAsync(
            message,
            review,
            CodeReviewExecutionOptions.Test,
            runtimeAgentsOverride: [runtimeAgent],
            cancellationToken
        );

        ApplyExecutionResult(review, result);

        var sourceTelemetry = CodeReviewSourceTelemetrySerializer.Deserialize(
            result.SourceTelemetryPayload
        );
        var findings = CodeReviewEndpointMapping.ToFindingsDto(
            review,
            result.Output,
            sourceTelemetry
        );

        return TypedResults.Ok(
            new CodeReviewAgentTestRunResponse(
                ToResultKind(result),
                CodeReviewEndpointMapping.ToDto(pullRequest),
                CodeReviewEndpointMapping.ToDto(review),
                findings,
                result.InScopeFileCount,
                result.OutOfScopeFileCount,
                result.ReviewerCount
            )
        );
    }

    private static CodeReviewEndpointError? Validate(RunCodeReviewAgentTestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PullRequestRecordId))
        {
            return new CodeReviewEndpointError(
                "missing_pull_request",
                "Pull request record id is required."
            );
        }

        if (request.PullRequestCreatedAtUtc == default)
        {
            return new CodeReviewEndpointError(
                "missing_pull_request_created_at",
                "Pull request createdAtUtc is required."
            );
        }

        if (request.Agent is null)
        {
            return new CodeReviewEndpointError(
                "invalid_agent_configuration",
                "Draft agent configuration is required."
            );
        }

        var agentError = CodeReviewerAgentEndpointValidation.Validate(request.Agent);
        return agentError is null
            ? null
            : new CodeReviewEndpointError("invalid_agent_configuration", agentError.Message);
    }

    private static CodeReviewRecord CreateSyntheticReview(PullRequestRecord pullRequest)
    {
        var now = DateTimeOffset.UtcNow;

        return new()
        {
            Id = $"synthetic_{Guid.CreateVersion7():N}",
            OrganizationId = pullRequest.OrganizationId,
            TeamId = pullRequest.TeamId,
            PullRequestRecordId = pullRequest.Id,
            RepositoryId = pullRequest.RepositoryId,
            OwnerQualifiedRepoName = pullRequest.OwnerQualifiedRepoName,
            PullRequestNumber = pullRequest.PullRequestNumber,
            Branch = pullRequest.Branch,
            Title = pullRequest.Title,
            AuthorLogin = pullRequest.AuthorLogin,
            Status = CodeReviewStatus.Running,
            RequestOrigin = CodeReviewRequestOrigin.Manual,
            ReviewGroupId = null,
            RemainingReviewBudget = 0,
            SourceTelemetryPayload = CodeReviewRecord.EmptySourceTelemetryPayload,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static CodeReviewRunRequested CreateSyntheticMessage(
        PullRequestRecord pullRequest,
        CodeReviewRecord review
    ) =>
        new()
        {
            OrganizationId = pullRequest.OrganizationId,
            TeamId = pullRequest.TeamId,
            RepositoryId = pullRequest.RepositoryId,
            OwnerQualifiedRepoName = pullRequest.OwnerQualifiedRepoName,
            PullRequestNumber = pullRequest.PullRequestNumber,
            PullRequestRecordId = pullRequest.Id,
            PullRequestCreatedAtUtc = pullRequest.CreatedAtUtc,
            CodeReviewRecordId = review.Id,
            CodeReviewCreatedAtUtc = review.CreatedAtUtc,
            GitHubDeliveryId = review.Id,
            TraceContext = ZeeqTelemetry.CaptureCurrentTraceContext(),
        };

    private static CodeReviewerRuntimeAgent ToRuntimeAgent(CreateCodeReviewerAgentRequest agent) =>
        new(
            "draft-agent",
            agent.DisplayName.Trim(),
            agent.ReviewFacet.Trim(),
            agent.ModelTier,
            agent.Prompt.Trim(),
            CodeReviewEndpointMapping.ToModel(agent.ActivationConfiguration)
        );

    private static void ApplyExecutionResult(
        CodeReviewRecord review,
        CodeReviewExecutionResult result
    )
    {
        review.Status = CodeReviewStatus.Completed;
        review.CriticalFindings = result.Counts.Critical;
        review.MajorFindings = result.Counts.Major;
        review.MinorFindings = result.Counts.Minor;
        review.SuggestionFindings = result.Counts.Suggestion;
        review.CommentFindings = result.Counts.Comment;
        review.SourceTelemetryPayload = result.SourceTelemetryPayload;
        review.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static CodeReviewAgentTestRunResultKind ToResultKind(CodeReviewExecutionResult result)
    {
        if (result.InScopeFileCount == 0)
        {
            return CodeReviewAgentTestRunResultKind.NoFilesInScope;
        }

        return result.NoAgentsActivated
            ? CodeReviewAgentTestRunResultKind.NoAgentActivation
            : CodeReviewAgentTestRunResultKind.Completed;
    }
}
