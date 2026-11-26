using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Request to set or clear one review finding.</summary>
public sealed record SaveFindingTriageRequest(
    [property: Required, MaxLength(100)] string FindingReference,
    CodeReviewFindingDisposition? Disposition,
    [property: MaxLength(1000)] string? Reason,
    bool Clear = false
);

/// <summary>Optional confirmation safeguards and replacement reason.</summary>
public sealed record ConfirmFindingTriageRequest(
    CodeReviewFindingDisposition? ExpectedDisposition,
    [property: MaxLength(1000)] string? Reason
);

/// <summary>Triage snapshot and link to its source, if retained.</summary>
public sealed record FindingTriageResponse(
    CodeReviewFindingTriageDto Triage,
    string? SingleReviewUrl,
    bool SourceReviewExpired
);

/// <summary>Provides authenticated access to finding triage.</summary>
public sealed class TriageFindingHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewRecordStore reviews,
    ICodeReviewFindingTriageStore triages,
    ICodeReviewFindingTriageService service,
    CodeReviewRequestLinkFactory links
) : IEndpointHandler
{
    /// <summary>Sets or clears a finding on a partition-pinned review.</summary>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewFindingTriageDto>>
    > SaveAsync(
        string orgId,
        string reviewId,
        DateTimeOffset? createdAtUtc,
        SaveFindingTriageRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var access = await authorization.ResolveAsync(orgId, user, ct);
        if (access is null)
            return TypedResults.NotFound();
        if (createdAtUtc is null)
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("missing_created_at", "createdAtUtc is required.")
            );
        if (
            string.IsNullOrWhiteSpace(request.FindingReference)
            || request.FindingReference.Length > 100
            || request.Reason?.Length > 1000
            || (!request.Clear && request.Disposition is null)
        )
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid",
                    "A finding reference and valid disposition are required."
                )
            );
        var review = await reviews.FindAsync(reviewId, createdAtUtc.Value, ct);
        if (review is null || review.OrganizationId != orgId)
            return TypedResults.NotFound();
        var result = await service.TriageAsync(
            new(
                orgId,
                new FindingTriageTarget.ByReview(reviewId, review.CreatedAtUtc),
                request.FindingReference,
                request.Clear ? FindingTriageOperation.Clear : FindingTriageOperation.Set,
                request.Disposition,
                request.Reason,
                CodeReviewFindingTriageChannel.Ui,
                access.UserId
            ),
            ct
        );
        return Map(result);
    }

    /// <summary>Loads a triage snapshot and its retained source link.</summary>
    public async Task<Results<NotFound, Ok<FindingTriageResponse>>> GetAsync(
        string orgId,
        string triageId,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (await authorization.ResolveAsync(orgId, user, ct) is null)
            return TypedResults.NotFound();
        var row = await triages.FindAsync(orgId, triageId, ct);
        if (row is null)
            return TypedResults.NotFound();
        var review = await reviews.FindAsync(row.SourceReviewId, row.SourceReviewCreatedAtUtc, ct);
        var retained = review is not null && review.OrganizationId == orgId;
        var url = retained
            ? links.BuildSingleReviewLink(
                review!,
                review!.PullRequestRecordId is null
                    ? CodeReviewSingleViewMode.Agent
                    : CodeReviewSingleViewMode.Pr
            )
            : null;
        return TypedResults.Ok(
            new FindingTriageResponse(CodeReviewEndpointMapping.ToTriageDto(row), url, !retained)
        );
    }

    /// <summary>Confirms a proposed decision.</summary>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewFindingTriageDto>>
    > ConfirmAsync(
        string orgId,
        string triageId,
        ConfirmFindingTriageRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        var access = await authorization.ResolveAsync(orgId, user, ct);
        if (access is null)
            return TypedResults.NotFound();
        if (request.Reason?.Length > 1000)
            return TypedResults.BadRequest(
                new CodeReviewEndpointError("invalid", "Reason exceeds 1000 characters.")
            );
        var result = await service.TriageAsync(
            new(
                orgId,
                new FindingTriageTarget.ByTriageId(triageId),
                "",
                FindingTriageOperation.Confirm,
                null,
                request.Reason,
                CodeReviewFindingTriageChannel.Ui,
                access.UserId,
                ExpectedDisposition: request.ExpectedDisposition
            ),
            ct
        );
        return Map(result);
    }

    /// <summary>Reopens a finding, including when the source review has expired.</summary>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewFindingTriageDto>>
    > ClearAsync(string orgId, string triageId, ClaimsPrincipal user, CancellationToken ct)
    {
        var access = await authorization.ResolveAsync(orgId, user, ct);
        if (access is null)
            return TypedResults.NotFound();
        var result = await service.TriageAsync(
            new(
                orgId,
                new FindingTriageTarget.ByTriageId(triageId),
                "",
                FindingTriageOperation.Clear,
                null,
                null,
                CodeReviewFindingTriageChannel.Ui,
                access.UserId
            ),
            ct
        );
        return Map(result);
    }

    private static Results<
        NotFound,
        BadRequest<CodeReviewEndpointError>,
        Ok<CodeReviewFindingTriageDto>
    > Map(FindingTriageOutcome result) =>
        result switch
        {
            FindingTriageOutcome.Applied applied => TypedResults.Ok(
                CodeReviewEndpointMapping.ToTriageDto(applied.Row)
            ),
            FindingTriageOutcome.NotFound => TypedResults.NotFound(),
            FindingTriageOutcome.Ambiguous => TypedResults.BadRequest(
                new CodeReviewEndpointError("ambiguous", "Finding reference is ambiguous.")
            ),
            FindingTriageOutcome.Forbidden forbidden => TypedResults.BadRequest(
                new CodeReviewEndpointError("forbidden", forbidden.Reason)
            ),
            FindingTriageOutcome.IgnoredStale => TypedResults.BadRequest(
                new CodeReviewEndpointError("stale", "The request is stale.")
            ),
            FindingTriageOutcome.Invalid invalid => TypedResults.BadRequest(
                new CodeReviewEndpointError("invalid", string.Join(" ", invalid.Errors))
            ),
        };
}
