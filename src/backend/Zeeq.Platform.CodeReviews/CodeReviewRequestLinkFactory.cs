using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Microsoft.Extensions.Options;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Builds frontend review request links for GitHub-rendered comments.
/// </summary>
/// <remarks>
/// Comment renderers need a complete absolute URL, not only an encrypted token,
/// because GitHub displays the link outside the Zeeq web app. Keeping URL
/// composition here gives future GitHub and non-GitHub renderers one place to
/// share the token format, expiry metadata, and frontend route convention.
/// </remarks>
public sealed class CodeReviewRequestLinkFactory(
    IOptions<AppSettings> appSettingsOptions,
    CodeReviewRequestTokenProtector tokenProtector
)
{
    /// <summary>
    /// Builds the link used by draft prompts before any review record exists.
    /// </summary>
    public CodeReviewRequestLink BuildInitialReviewLink(
        string organizationId,
        string? teamId,
        string repositoryId,
        string ownerQualifiedRepoName,
        int pullRequestNumber
    )
    {
        var appSettings = appSettingsOptions.Value;
        var remainingReviewBudget = appSettings.CodeReview.DefaultReviewBudget;
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(tokenProtector.GetValidity());
        var token = tokenProtector.ProtectInitialReview(
            organizationId,
            teamId,
            repositoryId,
            ownerQualifiedRepoName,
            pullRequestNumber,
            remainingReviewBudget,
            expiresAtUtc
        );

        return BuildLink(token, "initial", remainingReviewBudget, expiresAtUtc);
    }

    /// <summary>
    /// Builds the link used by completed-review comments for a later review request.
    /// </summary>
    /// <remarks>
    /// The link binds the exact partitioned review row so the future request
    /// endpoint can reload the source review and re-check the current budget
    /// before enqueueing another run. The visible budget query value is only a
    /// hint for rendering; the encrypted token and database state remain the
    /// authoritative inputs.
    /// </remarks>
    public CodeReviewRequestLink BuildExistingReviewLink(CodeReviewRecord review)
    {
        var remainingReviewBudget = review.RemainingReviewBudget;
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(tokenProtector.GetValidity());
        var token = tokenProtector.ProtectExistingReview(
            review,
            remainingReviewBudget,
            expiresAtUtc
        );

        return BuildLink(token, "rerequest", remainingReviewBudget, expiresAtUtc);
    }

    /// <summary>
    /// Builds an absolute URL for a public frontend asset.
    /// </summary>
    public string BuildPublicAssetLink(string relativePath)
    {
        var appSettings = appSettingsOptions.Value;
        var baseUri = appSettings.Http.FrontendBaseUri.TrimEnd('/');
        var path = relativePath.TrimStart('/');

        return $"{baseUri}/{path}";
    }

    /// <summary>
    /// Builds an absolute frontend URL to the single code-review view for one review row.
    /// The view is auth-gated by the app cookie/session, so the link carries no secret —
    /// just a compact, URL-safe token pairing the partition timestamp with the view mode
    /// (see <see cref="CodeReviewSingleViewToken"/>).
    /// </summary>
    public string BuildSingleReviewLink(CodeReviewRecord review, CodeReviewSingleViewMode mode) =>
        BuildSingleReviewLink(review.Id, review.CreatedAtUtc, mode);

    /// <summary>
    /// Overload of <see cref="BuildSingleReviewLink(CodeReviewRecord, CodeReviewSingleViewMode)"/>
    /// for callers that only have the review's id/timestamp from a projection query, not the full
    /// loaded entity (for example the findings drill-down list, which selects a handful of columns
    /// across many rows rather than materializing entities).
    /// </summary>
    public string BuildSingleReviewLink(
        string reviewId,
        DateTimeOffset reviewCreatedAtUtc,
        CodeReviewSingleViewMode mode
    )
    {
        var baseUri = appSettingsOptions.Value.Http.FrontendBaseUri.TrimEnd('/');
        var token = CodeReviewSingleViewToken.Encode(reviewCreatedAtUtc, mode);

        return $"{baseUri}/code-reviews/reviews/{reviewId}?c={token}";
    }

    /// <summary>
    /// Builds an absolute frontend URL to the single pull-request view, which renders a PR's full
    /// review history (every attempt), not just one review row.
    /// </summary>
    /// <remarks>
    /// Takes the raw id/timestamp rather than a loaded <see cref="PullRequestRecord"/> because
    /// callers (such as the findings drill-down list) often only have these two columns from a
    /// projection query, not the full entity. The token is keyed by the pull request's own
    /// <c>CreatedAtUtc</c> (its partition timestamp), not any review's — mirrors the pattern
    /// <c>CheckRunService.BuildPrViewLink</c> uses for check-run comment links.
    /// </remarks>
    public string BuildSinglePullRequestLink(
        string pullRequestRecordId,
        DateTimeOffset pullRequestRecordCreatedAtUtc
    )
    {
        var baseUri = appSettingsOptions.Value.Http.FrontendBaseUri.TrimEnd('/');
        var token = CodeReviewSingleViewToken.Encode(
            pullRequestRecordCreatedAtUtc,
            CodeReviewSingleViewMode.Pr
        );

        return $"{baseUri}/code-reviews/pull-requests/{pullRequestRecordId}/single?c={token}";
    }

    private CodeReviewRequestLink BuildLink(
        string token,
        string kind,
        int remainingReviewBudget,
        DateTimeOffset expiresAtUtc
    )
    {
        var appSettings = appSettingsOptions.Value;
        var baseUri = appSettings.Http.FrontendBaseUri.TrimEnd('/');
        var query =
            $"request={Uri.EscapeDataString(token)}"
            + $"&kind={Uri.EscapeDataString(kind)}"
            + $"&remaining={remainingReviewBudget.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        return new CodeReviewRequestLink(
            Url: $"{baseUri}/code-reviews?{query}",
            RemainingReviewBudget: remainingReviewBudget,
            ExpiresAtUtc: expiresAtUtc
        );
    }
}

/// <summary>
/// Frontend review request link plus rendering metadata.
/// </summary>
/// <param name="Url">Absolute frontend URL safe to place in a GitHub comment.</param>
/// <param name="RemainingReviewBudget">Budget value shown when the link was rendered.</param>
/// <param name="ExpiresAtUtc">Hard expiry embedded in the encrypted token.</param>
public sealed record CodeReviewRequestLink(
    string Url,
    int RemainingReviewBudget,
    DateTimeOffset ExpiresAtUtc
);
