namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles minimal review update polling for the PR inbox.
/// </summary>
public sealed class ListCodeReviewInboxUpdatesHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewRecordStore reviews
) : IEndpointHandler
{
    /// <summary>
    /// Lists review updates by update high-water mark and review partition lower bound.
    /// </summary>
    public async Task<
        Results<
            NotFound,
            BadRequest<CodeReviewEndpointError>,
            Ok<CodeReviewInboxUpdateListResponse>
        >
    > HandleAsync(
        string organizationId,
        string? teamId,
        string? repositoryId,
        CodeReviewInboxScope? scope,
        DateTimeOffset? reviewCreatedAtLowerBoundUtc,
        DateTimeOffset? cursorUpdatedAtUtc,
        DateTimeOffset? cursorCreatedAtUtc,
        string? cursorId,
        string? cursorTeamId,
        string? cursorRepositoryId,
        CodeReviewInboxScope? cursorScope,
        string? cursorSubjectUserId,
        int? pageSize,
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

        var cursor = CodeReviewEndpointMapping.ToUpdateCursor(
            reviewCreatedAtLowerBoundUtc,
            cursorUpdatedAtUtc,
            cursorCreatedAtUtc,
            cursorId,
            cursorTeamId,
            cursorRepositoryId,
            cursorScope,
            cursorSubjectUserId
        );
        if (cursor is null && reviewCreatedAtLowerBoundUtc is null)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "missing_review_created_lower_bound",
                    "reviewCreatedAtLowerBoundUtc or a complete update cursor is required."
                )
            );
        }

        var effectiveTeamId =
            CodeReviewEndpointMapping.NormalizeOptionalFilter(teamId) ?? cursor?.TeamId;
        var effectiveRepositoryId =
            CodeReviewEndpointMapping.NormalizeOptionalFilter(repositoryId) ?? cursor?.RepositoryId;
        var effectiveScope = scope ?? cursor?.Scope ?? CodeReviewInboxScope.All;
        var effectiveSubjectUserId =
            effectiveScope == CodeReviewInboxScope.Mine
                ? user.FindFirstValue(OpenIddictConstants.Claims.Subject)
                : null;

        if (
            cursor is not null
            && !cursor.Matches(
                effectiveTeamId,
                effectiveRepositoryId,
                effectiveScope,
                effectiveSubjectUserId
            )
        )
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "cursor_filter_mismatch",
                    "Inbox update cursors can only resume the same team, repository, and user scope."
                )
            );
        }

        if (await authorization.ResolveAsync(organizationId, user, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var page = await reviews.ListInboxUpdatesAsync(
            new CodeReviewUpdateStreamQuery(
                OrganizationId: organizationId,
                TeamId: effectiveTeamId,
                RepositoryId: effectiveRepositoryId,
                Scope: effectiveScope,
                SubjectUserId: effectiveSubjectUserId,
                ReviewCreatedAtLowerBoundUtc: reviewCreatedAtLowerBoundUtc,
                Cursor: cursor,
                PageSize: pageSize ?? 50
            ),
            cancellationToken
        );

        return TypedResults.Ok(
            new CodeReviewInboxUpdateListResponse(
                page.Items.Select(CodeReviewEndpointMapping.ToDto).ToArray(),
                CodeReviewEndpointMapping.ToDto(page.NextCursor),
                CodeReviewEndpointMapping.ToDto(page.NewestCursor)
            )
        );
    }
}
