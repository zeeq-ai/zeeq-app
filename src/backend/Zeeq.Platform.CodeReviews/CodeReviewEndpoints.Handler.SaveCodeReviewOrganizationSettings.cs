namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles saving organization-level code-review execution settings.
/// </summary>
public sealed class SaveCodeReviewOrganizationSettingsHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewOrganizationSettingsStore settingsStore
) : IEndpointHandler
{
    internal const int MinimumMaxConcurrentReviews = 1;
    internal const int MaximumMaxConcurrentReviews = 8;

    /// <summary>
    /// Saves code-review execution capacity for one organization.
    /// </summary>
    /// <remarks>
    /// This endpoint intentionally edits only <c>MaxConcurrentReviews</c>.
    /// Execution lease duration remains an internal operational setting until
    /// product has a UI requirement for exposing it.
    /// </remarks>
    public async Task<
        Results<
            NotFound,
            ForbidHttpResult,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewOrganizationSettingsResponse>
        >
    > HandleAsync(
        string organizationId,
        SaveCodeReviewOrganizationSettingsRequest request,
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

        var validationError = Validate(request);
        if (validationError is not null)
        {
            return TypedResults.BadRequest(validationError);
        }

        var settings = await settingsStore.GetAsync(organizationId, cancellationToken);
        var updated = settings with { MaxConcurrentReviews = request.MaxConcurrentReviews };

        var saved = await settingsStore.SaveAsync(organizationId, updated, cancellationToken);

        return TypedResults.Ok(CodeReviewEndpointMapping.ToDto(organizationId, saved));
    }

    private static CodeReviewEndpointError? Validate(
        SaveCodeReviewOrganizationSettingsRequest request
    )
    {
        // NOTE: A billing/product entitlement service does not exist in this
        // slice yet. The fixed ceiling prevents unbounded organization
        // concurrency while leaving room for the intended 1/2/4 operational
        // tiers until product-tier constraints are wired explicitly.
        return
            request.MaxConcurrentReviews
                is < MinimumMaxConcurrentReviews
                    or > MaximumMaxConcurrentReviews
            ? new CodeReviewEndpointError(
                "invalid_code_review_settings",
                $"Max concurrent reviews must be between {MinimumMaxConcurrentReviews} and {MaximumMaxConcurrentReviews}."
            )
            : null;
    }
}
