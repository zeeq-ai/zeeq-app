using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles loading a single code review plus the related set for the deep-link view.
/// </summary>
public sealed class GetCodeReviewHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewRecordStore reviews,
    IPullRequestLookupStore pullRequestLookups,
    IPullRequestRecordStore pullRequests
) : IEndpointHandler
{
    /// <summary>
    /// Gets one review row plus the related review set, newest first with the primary
    /// review as the first item.
    /// </summary>
    /// <remarks>
    /// Review rows are partitioned by creation timestamp. The <paramref name="mode"/> query
    /// parameter controls the related set: <c>pr</c> for the parent PR's review history,
    /// <c>agent</c> for the agent session's reviews. When omitted the origin of the review
    /// row (<see cref="CodeReviewRequestOrigin"/>) determines the default.
    /// </remarks>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewSingleViewResponse>>
    > HandleAsync(
        string organizationId,
        string codeReviewRecordId,
        DateTimeOffset? createdAtUtc,
        CodeReviewSingleViewMode? mode,
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
                    "createdAtUtc is required for partition-aware review lookup."
                )
            );
        }

        if (await authorization.ResolveAsync(organizationId, user, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var review = await reviews.FindAsync(
            codeReviewRecordId,
            createdAtUtc.Value,
            cancellationToken
        );
        if (review is null || review.OrganizationId != organizationId)
        {
            return TypedResults.NotFound();
        }

        var resolvedMode = mode ?? DefaultModeFor(review.RequestOrigin);

        // Resolve the effective mode once, then delegate to a mode-specific loader so PR and
        // agent history stay independent and adding a future mode is a localized change.
        IReadOnlyList<CodeReviewRecord> relatedReviews = resolvedMode switch
        {
            CodeReviewSingleViewMode.Agent => await LoadAgentHistoryAsync(
                organizationId,
                review,
                cancellationToken
            ),
            CodeReviewSingleViewMode.Pr => await LoadPrHistoryAsync(
                organizationId,
                review,
                cancellationToken
            ),
            _ => [],
        };

        var ordered = EnsurePrimaryFirst(review, relatedReviews);
        var pullRequest = await LoadPullRequestAsync(organizationId, review, cancellationToken);

        return TypedResults.Ok(
            new CodeReviewSingleViewResponse(
                CodeReviewEndpointMapping.ToDto(review),
                [.. ordered.Select(CodeReviewEndpointMapping.ToDto)],
                resolvedMode,
                pullRequest
            )
        );
    }

    /// <summary>
    /// Resolves the reviewed pull request's current DTO, when the review is tied to one, so
    /// the deep-link view can render GitHub/bypass actions that depend on live PR state.
    /// </summary>
    private async Task<CodeReviewPullRequestDto?> LoadPullRequestAsync(
        string organizationId,
        CodeReviewRecord review,
        CancellationToken cancellationToken
    )
    {
        if (review.PullRequestRecordId is not { } pullRequestRecordId)
        {
            return null;
        }

        if (review.RepositoryId is not { } repoId)
        {
            return null;
        }

        var lookup = await pullRequestLookups.FindAsync(
            organizationId,
            repoId,
            review.PullRequestNumber,
            cancellationToken
        );
        if (lookup is null)
        {
            return null;
        }

        var pullRequest = await pullRequests.FindAsync(
            pullRequestRecordId,
            lookup.PullRequestCreatedAtUtc,
            cancellationToken
        );

        return pullRequest is null ? null : CodeReviewEndpointMapping.ToDto(pullRequest);
    }

    private Task<IReadOnlyList<CodeReviewRecord>> LoadAgentHistoryAsync(
        string organizationId,
        CodeReviewRecord review,
        CancellationToken cancellationToken
    ) =>
        reviews.ListForAgentAsync(
            organizationId,
            review.AgentSessionId,
            review.ReviewGroupId,
            maxRecords: 50,
            cancellationToken
        );

    private async Task<IReadOnlyList<CodeReviewRecord>> LoadPrHistoryAsync(
        string organizationId,
        CodeReviewRecord review,
        CancellationToken cancellationToken
    )
    {
        if (review.PullRequestRecordId is not { } pullRequestRecordId)
        {
            return [];
        }

        if (review.RepositoryId is not { } repoId)
        {
            return [];
        }

        var lookup = await pullRequestLookups.FindAsync(
            organizationId,
            repoId,
            review.PullRequestNumber,
            cancellationToken
        );
        if (lookup is null)
        {
            // NOTE: PR lookup is required to resolve the partition key; querying with
            // DateTimeOffset.MinValue can target the wrong partition.
            return [];
        }

        var page = await reviews.ListForPullRequestAsync(
            new PullRequestReviewStreamQuery(
                OrganizationId: organizationId,
                PullRequestRecordId: pullRequestRecordId,
                PullRequestCreatedAtUtc: lookup.PullRequestCreatedAtUtc,
                Cursor: null,
                PageSize: 50
            ),
            cancellationToken
        );

        return page.Items;
    }

    private static CodeReviewSingleViewMode DefaultModeFor(CodeReviewRequestOrigin origin) =>
        origin == CodeReviewRequestOrigin.Agent
            ? CodeReviewSingleViewMode.Agent
            : CodeReviewSingleViewMode.Pr;

    private static IReadOnlyList<CodeReviewRecord> EnsurePrimaryFirst(
        CodeReviewRecord primary,
        IReadOnlyList<CodeReviewRecord> related
    )
    {
        var list = new List<CodeReviewRecord>(related.Count + 1);
        var seenIds = new HashSet<string>(StringComparer.Ordinal) { primary.Id };
        list.Add(primary);

        foreach (var item in related)
        {
            if (seenIds.Add(item.Id))
            {
                list.Add(item);
            }
        }

        return list;
    }
}
