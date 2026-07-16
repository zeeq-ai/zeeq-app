using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for loading previous review outputs for agent prompt context.
/// </summary>
internal sealed class CodeReviewPreviousReviewStore(
    PostgresDbContext db,
    CodeReviewXmlOutputValidator xmlValidator,
    ICodeReviewArtifactStore artifacts,
    ILogger<CodeReviewPreviousReviewStore> logger
) : ICodeReviewPreviousReviewStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewPreviousReview>> LoadForAgentAsync(
        string organizationId,
        string? agentSessionId,
        string? reviewGroupId,
        string excludeReviewId,
        int maxRecords = 3,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(agentSessionId) && string.IsNullOrEmpty(reviewGroupId))
        {
            return [];
        }

        var query = db
            .CodeReviewRecords.TagWithOperationCallSite(
                "code_review_previous_review.load_for_agent"
            )
            .AsNoTracking()
            .Where(r =>
                r.OrganizationId == organizationId
                && r.Status == CodeReviewStatus.Completed
                && r.Id != excludeReviewId
            );

        // Either-key rule: match when session id OR group id matches (or both).
        query = query.Where(r =>
            (agentSessionId != null && r.AgentSessionId == agentSessionId)
            || (reviewGroupId != null && r.ReviewGroupId == reviewGroupId)
        );

        var records = await query
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(Math.Clamp(maxRecords, 1, 5))
            .ToArrayAsync(cancellationToken);

        if (records.Length == 0)
        {
            return [];
        }

        var results = new List<CodeReviewPreviousReview>(records.Length);

        foreach (var record in records)
        {
            var parsed = await TryParseRecordAsync(record, cancellationToken);

            if (parsed is not null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewPreviousReview>> LoadAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string reviewGroupId,
        string excludeReviewId,
        int maxRecords = 3,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(reviewGroupId))
        {
            return [];
        }

        var records = await db
            .CodeReviewRecords.TagWithOperationCallSite("code_review_previous_review.load")
            .AsNoTracking()
            .Where(r =>
                r.OrganizationId == organizationId
                && r.OwnerQualifiedRepoName == ownerQualifiedRepoName
                && r.PullRequestNumber == pullRequestNumber
                && r.ReviewGroupId == reviewGroupId
                && r.Status == CodeReviewStatus.Completed
                && r.Id != excludeReviewId
            )
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(Math.Clamp(maxRecords, 1, 5))
            .ToArrayAsync(cancellationToken);

        if (records.Length == 0)
        {
            return [];
        }

        var results = new List<CodeReviewPreviousReview>(records.Length);

        foreach (var record in records)
        {
            var parsed = await TryParseRecordAsync(record, cancellationToken);

            if (parsed is not null)
            {
                results.Add(parsed);
            }
        }

        return results;
    }

    private async Task<CodeReviewPreviousReview?> TryParseRecordAsync(
        CodeReviewRecord record,
        CancellationToken cancellationToken
    )
    {
        if (record.FindingsStorageUri is null)
        {
            logger.LogWarning(
                "Previous code review record {CodeReviewId} has no findings storage URI.",
                record.Id
            );
            return null;
        }

        try
        {
            using var stream = await artifacts.OpenFindingsAsync(
                record.FindingsStorageUri,
                cancellationToken
            );

            using var reader = new StreamReader(stream);

            var xml = await reader.ReadToEndAsync(cancellationToken);

            var validation = xmlValidator.Validate(xml);

            if (!validation.IsValid || validation.Output is null)
            {
                logger.LogWarning(
                    "Failed to parse previous code review XML for record {CodeReviewId}: {ErrorMessage}",
                    record.Id,
                    validation.ErrorMessage ?? "Unknown error"
                );
                return null;
            }

            // Map the first facet output to a CodeReviewPreviousReview.
            // Each review record typically has one facet per reviewer.
            var facet = validation.Output.Reviews.FirstOrDefault();

            if (facet is null)
            {
                return null;
            }

            return new CodeReviewPreviousReview(
                Facet: facet.Facet,
                Summary: facet.Summary,
                Findings:
                [
                    .. facet.Findings.Select(f => new CodeReviewPreviousFinding(
                        Summary: f.Summary,
                        Details: f.Details,
                        File: f.File,
                        Level: f.Level
                    )),
                ]
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to parse previous code review XML for record {CodeReviewId}",
                record.Id
            );
            return null;
        }
    }
}
