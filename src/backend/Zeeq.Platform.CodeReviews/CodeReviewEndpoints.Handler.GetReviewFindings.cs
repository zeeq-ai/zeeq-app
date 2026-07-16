using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Handles loading parsed finding details for one code review record.
/// </summary>
public sealed class GetCodeReviewFindingsHandler(
    CodeReviewAuthorization authorization,
    ICodeReviewRecordStore reviews,
    ICodeReviewArtifactStore artifacts,
    CodeReviewXmlOutputValidator xmlValidator
) : IEndpointHandler
{
    /// <summary>
    /// Gets one review's finding artifact as typed reviewer and finding DTOs.
    /// </summary>
    /// <remarks>
    /// Review rows are partitioned by creation timestamp, so callers must provide
    /// <paramref name="createdAtUtc" />. Reviews with zero aggregate findings
    /// intentionally return an empty payload without opening artifact storage;
    /// the frontend uses the same rule to avoid loading clean review artifacts.
    /// </remarks>
    public async Task<
        Results<NotFound, BadRequest<CodeReviewEndpointError>, Ok<CodeReviewFindingsResponse>>
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

        // Load telemetry regardless of finding count so a clean-PR review still shows how the
        // conclusion was reached. Best-effort: a malformed payload deserializes to null.
        var sourceTelemetry = CodeReviewSourceTelemetrySerializer.Deserialize(
            review.SourceTelemetryPayload
        );

        if (TotalFindings(review) == 0)
        {
            return TypedResults.Ok(
                CodeReviewEndpointMapping.ToEmptyFindingsDto(review, sourceTelemetry)
            );
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
        var validation = xmlValidator.Validate(findingsXml);

        if (!validation.IsValid || validation.Output is null)
        {
            return TypedResults.BadRequest(
                new CodeReviewEndpointError(
                    "invalid_findings_artifact",
                    validation.ErrorMessage ?? "Code review findings artifact could not be parsed."
                )
            );
        }

        return TypedResults.Ok(
            CodeReviewEndpointMapping.ToFindingsDto(review, validation.Output, sourceTelemetry)
        );
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

    private static int TotalFindings(CodeReviewRecord review) =>
        review.CriticalFindings
        + review.MajorFindings
        + review.MinorFindings
        + review.SuggestionFindings
        + review.CommentFindings;
}
