using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Lists saved reviewer configuration revisions.
/// </summary>
/// <remarks>
/// Viewing history is a read, but an agent that predates revisions gets its current
/// configuration snapshotted on first view. That is the only write this handler performs, and
/// it never modifies the agent or the live prompt.
/// </remarks>
public sealed class ListCodeReviewerAgentRevisionsHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewerAgentStore agents,
    ICodeReviewerAgentRevisionStore revisions,
    TimeProvider timeProvider
) : IEndpointHandler
{
    private const int MaxPageSize = 200;

    /// <summary>Handles the list request.</summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewerAgentRevisionListResponse>
        >
    > HandleAsync(
        string organizationId,
        string agentId,
        int? pageSize,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var access = await authorization.ResolveAsync(organizationId, user, cancellationToken);
        if (access is null)
        {
            return TypedResults.NotFound();
        }

        if (!access.CanManage)
        {
            return TypedResults.Forbid();
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_request",
                    $"Page size must be between 1 and {MaxPageSize}."
                )
            );
        }

        var agent = await agents.FindAsync(organizationId, agentId, cancellationToken);
        if (agent is null)
        {
            return TypedResults.NotFound();
        }

        var userId = CodeReviewEvaluationHandlerSupport.UserId(user) ?? string.Empty;
        await revisions.EnsureInitialSnapshotAsync(
            agent,
            userId,
            timeProvider.GetUtcNow(),
            cancellationToken
        );

        var rows = await revisions.ListForAgentAsync(
            organizationId,
            agent.Id,
            cursorId: null,
            pageSize ?? MaxPageSize,
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewerAgentRevisionListResponse([
                .. rows.Select(CodeReviewEvaluationEndpointMapping.ToSummaryDto),
            ])
        );
    }
}

/// <summary>
/// Returns one saved reviewer configuration revision.
/// </summary>
public sealed class GetCodeReviewerAgentRevisionHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewerAgentStore agents,
    ICodeReviewerAgentRevisionStore revisions
) : IEndpointHandler
{
    /// <summary>Handles the get request.</summary>
    public async Task<
        Results<NotFound, ForbidHttpResult, Ok<CodeReviewerAgentRevisionResponse>>
    > HandleAsync(
        string organizationId,
        string agentId,
        string revisionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var access = await authorization.ResolveAsync(organizationId, user, cancellationToken);
        if (access is null)
        {
            return TypedResults.NotFound();
        }

        if (!access.CanManage)
        {
            return TypedResults.Forbid();
        }

        var agent = await agents.FindAsync(organizationId, agentId, cancellationToken);
        if (agent is null)
        {
            return TypedResults.NotFound();
        }

        var revision = await revisions.FindAsync(
            organizationId,
            agent.Id,
            revisionId,
            cancellationToken
        );
        if (revision is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(
            new CodeReviewerAgentRevisionResponse(
                CodeReviewEvaluationEndpointMapping.ToDto(revision)
            )
        );
    }
}
