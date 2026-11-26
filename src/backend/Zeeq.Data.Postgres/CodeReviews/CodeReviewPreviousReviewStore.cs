using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

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
        string ownerQualifiedRepoName,
        int maxRecords = 3,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrEmpty(reviewGroupId))
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

        // Review groups can span repositories; never import another repository's history.
        if (string.IsNullOrWhiteSpace(ownerQualifiedRepoName))
            return [];
        query = query.Where(r =>
            r.ReviewGroupId == reviewGroupId
            && r.OwnerQualifiedRepoName.ToLower() == ownerQualifiedRepoName.ToLower()
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

            results.AddRange(parsed);
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

            results.AddRange(parsed);
        }

        return results;
    }

    private async Task<IReadOnlyList<CodeReviewPreviousReview>> TryParseRecordAsync(
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
            return [];
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
                return [];
            }

            // The default is three records, capped at five, not a cap on facets or findings.
            // Prompt rendering uses summaries to reduce growth while preserving every reviewer
            // perspective on both MCP and PR runs; individual record size is not bounded here.
            // Ship this with the independent-pass rule to avoid increasing anchoring.
            return validation
                .Output.Reviews.Select(facet => new CodeReviewPreviousReview(
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
                ))
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to parse previous code review XML for record {CodeReviewId}",
                record.Id
            );
            return [];
        }
    }
}
