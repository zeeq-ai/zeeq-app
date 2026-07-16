namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles reading repository-level code-review configuration.
/// </summary>
public sealed class GetRepositoryReviewConfigurationHandler(
    CodeReviewAuthorization authorization,
    ICodeRepositoryStore repositories
) : IEndpointHandler
{
    /// <summary>
    /// Gets the typed review configuration for one configured repository.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewRepositoryConfigurationResponse>
        >
    > HandleAsync(
        string organizationId,
        string repositoryId,
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

        if (await authorization.ResolveAsync(organizationId, user, cancellationToken) is null)
        {
            return TypedResults.NotFound();
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

        return TypedResults.Ok(
            new CodeReviewRepositoryConfigurationResponse(
                repository.Id,
                CodeReviewEndpointMapping.ToDto(repository.ReviewConfiguration)
            )
        );
    }
}
