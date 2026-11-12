using Microsoft.AspNetCore.Authorization;
using Zeeq.Core.Common;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Reports whether this deployment admits reviewer evaluations.</summary>
public sealed class CodeReviewEvaluationAvailabilityEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        app.MapGroup("orgs/{orgId}/code-reviews")
            .WithTags("Code Reviews")
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            .RequireRouteOrganizationMatchesCookie()
            .MapGet(
                "/evaluation-availability",
                static (
                    string orgId,
                    ClaimsPrincipal user,
                    [FromServices] GetCodeReviewEvaluationAvailabilityHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(orgId, user, ct)
            )
            .WithName("GetCodeReviewEvaluationAvailability")
            .WithSummary("Get reviewer evaluation availability.");
    }
}

/// <summary>Authorizes the management UI before exposing evaluation availability.</summary>
public sealed class GetCodeReviewEvaluationAvailabilityHandler(
    CodeReviewAuthorization authorization,
    CodeReviewSettings settings
) : IEndpointHandler
{
    /// <summary>Returns deployment availability without resolving any provider credentials.</summary>
    public async Task<
        Results<NotFound, ForbidHttpResult, Ok<CodeReviewEvaluationAvailabilityResponse>>
    > HandleAsync(string organizationId, ClaimsPrincipal user, CancellationToken cancellationToken)
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

        return TypedResults.Ok(
            new CodeReviewEvaluationAvailabilityResponse(
                settings.EvaluationsEnabled,
                CodeReviewEvaluationLimits.MaxCases,
                CodeReviewEvaluationLimits.MaxConcurrentCases
            )
        );
    }
}

/// <summary>Non-secret rollout availability and fixed evaluation limits.</summary>
public sealed record CodeReviewEvaluationAvailabilityResponse(
    bool Enabled,
    int MaxCases,
    int MaxConcurrentCases
);
