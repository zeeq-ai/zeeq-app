using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles loading the unparsed findings artifact for one code review record.
/// </summary>
public sealed class GetCodeReviewRawFindingsHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewRecordStore reviews,
    ICodeReviewArtifactStore artifacts
) : IEndpointHandler
{
    /// <summary>
    /// Gets one review's finding artifact as the raw XML the reviewer agents produced.
    /// </summary>
    /// <remarks>
    /// Review rows are partitioned by creation timestamp, so callers must provide
    /// <paramref name="createdAtUtc" />. Unlike <see cref="GetCodeReviewFindingsHandler"/>,
    /// this always opens artifact storage when a URI is present — a clean review can still
    /// carry reviewer summaries worth inspecting in the raw artifact.
    /// </remarks>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewRawFindingsResponse>>
    > HandleAsync(
        string organizationId,
        string codeReviewRecordId,
        DateTimeOffset? createdAtUtc,
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
                    "createdAtUtc is required for partition-aware review findings lookup."
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

        if (string.IsNullOrWhiteSpace(review.FindingsStorageUri))
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "missing_findings_artifact",
                    "Code review findings cannot be loaded because the review has no findings artifact."
                )
            );
        }

        var findingsXml = await ReadFindingsXmlAsync(review.FindingsStorageUri, cancellationToken);

        return TypedResults.Ok(new CodeReviewRawFindingsResponse(findingsXml));
    }

    private async Task<string> ReadFindingsXmlAsync(
        string findingsStorageUri,
        CancellationToken cancellationToken
    )
    {
        await using var stream = await artifacts.OpenFindingsAsync(
            findingsStorageUri,
            cancellationToken
        );
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(cancellationToken);
    }
}
