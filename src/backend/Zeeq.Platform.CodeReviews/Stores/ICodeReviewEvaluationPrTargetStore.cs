using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Read store for known pull requests offered as evaluation targets.
/// </summary>
/// <remarks>
/// This is a read-only projection over the existing pull request records. It never creates
/// or mutates PR rows; unknown pull requests enter a dataset through provider capture, not by
/// fabricating production history.
/// </remarks>
public interface ICodeReviewEvaluationPrTargetStore
{
    /// <summary>Searches known pull requests for one repository, newest first.</summary>
    Task<CodeReviewEvaluationPrTargetPage> SearchAsync(
        CodeReviewEvaluationPrTargetQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>Finds one known pull request by provider number.</summary>
    Task<CodeReviewEvaluationPrTargetRecord?> FindAsync(
        string organizationId,
        string repositoryId,
        int pullRequestNumber,
        CancellationToken cancellationToken
    );
}

/// <summary>Filter for the evaluation PR target picker.</summary>
/// <param name="OrganizationId">Organization scope.</param>
/// <param name="RepositoryId">Repository mapping scope.</param>
/// <param name="Query">Optional title fragment or pull request number.</param>
/// <param name="CursorCreatedAtUtc">Cursor creation timestamp from the previous page.</param>
/// <param name="CursorId">Cursor row id from the previous page.</param>
/// <param name="PageSize">Requested page size.</param>
public sealed record CodeReviewEvaluationPrTargetQuery(
    string OrganizationId,
    string RepositoryId,
    string? Query = null,
    DateTimeOffset? CursorCreatedAtUtc = null,
    string? CursorId = null,
    int PageSize = 50
);

/// <summary>One known pull request row for the evaluation picker.</summary>
/// <param name="PullRequestRecordId">Partitioned pull request record id.</param>
/// <param name="CreatedAtUtc">Pull request record creation timestamp.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="Title">Current provider title.</param>
/// <param name="State">Current provider state.</param>
/// <param name="HeadSha">Current head commit identity.</param>
/// <param name="AuthorLogin">Provider login of the author.</param>
/// <param name="UpdatedAtUtc">Most recent update timestamp.</param>
public sealed record CodeReviewEvaluationPrTargetRecord(
    string PullRequestRecordId,
    DateTimeOffset CreatedAtUtc,
    int PullRequestNumber,
    string Title,
    PullRequestState State,
    string HeadSha,
    string AuthorLogin,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>Page of known pull request targets.</summary>
/// <param name="Items">Matching rows, newest first.</param>
/// <param name="NextCursorCreatedAtUtc">Cursor creation timestamp for the next page.</param>
/// <param name="NextCursorId">Cursor row id for the next page.</param>
public sealed record CodeReviewEvaluationPrTargetPage(
    IReadOnlyList<CodeReviewEvaluationPrTargetRecord> Items,
    DateTimeOffset? NextCursorCreatedAtUtc,
    string? NextCursorId
);
