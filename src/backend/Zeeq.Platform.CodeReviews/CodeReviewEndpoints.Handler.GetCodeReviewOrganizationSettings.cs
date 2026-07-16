namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles reading organization-level code-review execution settings.
/// </summary>
public sealed class GetCodeReviewOrganizationSettingsHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewOrganizationSettingsStore settingsStore
) : IEndpointHandler
{
    /// <summary>
    /// Gets the effective code-review execution settings for one organization.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewOrganizationSettingsResponse>
        >
    > HandleAsync(string organizationId, ClaimsPrincipal user, CancellationToken cancellationToken)
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

        var settings = await settingsStore.GetAsync(organizationId, cancellationToken);

        return TypedResults.Ok(CodeReviewEndpointMapping.ToDto(organizationId, settings));
    }
}
