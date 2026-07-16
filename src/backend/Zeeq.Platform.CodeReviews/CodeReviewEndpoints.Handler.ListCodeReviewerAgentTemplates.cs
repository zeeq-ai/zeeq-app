using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles listing the built-in, clonable reviewer-agent templates.
/// </summary>
/// <remarks>
/// Templates are code-defined content, not persisted rows, so the handler needs
/// no store. It is still organization-scoped and management-gated so the catalog
/// is only exposed to operators who can create agents, keeping the surface
/// consistent with the sibling agent-management endpoints.
/// </remarks>
public sealed class ListCodeReviewerAgentTemplatesHandler(CodeReviewAuthorization authorization)
    : IEndpointHandler
{
    /// <summary>
    /// Returns the built-in reviewer-agent template catalog for cloning.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewerAgentTemplateListResponse>
        >
    > HandleAsync(
        string organizationId,
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

        // NOTE: Read-only catalog fetch, so it mirrors the ListRepositoryCodeReviewerAgents
        // gate (CanManage only) and deliberately omits RequireActiveOrganization. That
        // activation filter redirects suspended orgs and is reserved for mutation/cost-bearing
        // endpoints. A future generate-an-agent endpoint (which spends LLM budget) is where
        // RequireActiveOrganization plus review-budget guards belong, not on this listing.
        return TypedResults.Ok(
            new CodeReviewerAgentTemplateListResponse(
                CodeReviewerAgentTemplateLibrary.All.Select(CodeReviewEndpointMapping.ToDto).ToArray()
            )
        );
    }
}
