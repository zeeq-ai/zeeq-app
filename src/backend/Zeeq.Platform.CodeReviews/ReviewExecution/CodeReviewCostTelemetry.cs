using System.Diagnostics.Metrics;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>Emits one cost measurement after a durable review reaches a terminal state.</summary>
internal static class CodeReviewCostTelemetry
{
    private static readonly Histogram<double> ReviewCostHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<double>(
            "zeeq_review_cost_usd",
            "Estimated API-rate USD cost of all model calls in one code review."
        );

    public static void Record(CodeReviewRecord review)
    {
        if (review.EstimatedCostUsd is not { } costUsd)
        {
            return;
        }

        ReviewCostHistogram.Record(
            (double)costUsd,
            [
                new("organization_id", review.OrganizationId),
                new("repository_id", review.RepositoryId),
                new("review_id", review.Id),
                new(
                    "view_token",
                    CodeReviewSingleViewToken.Encode(
                        review.CreatedAtUtc,
                        review.PullRequestRecordId is null
                            ? CodeReviewSingleViewMode.Agent
                            : CodeReviewSingleViewMode.Pr
                    )
                ),
                new("author_login", review.AuthorLogin),
                new("request_origin", review.RequestOrigin.ToString()),
                new("status", review.Status.ToString()),
                new("cost_catalog_version", review.CostCatalogVersion),
            ]
        );
    }
}
