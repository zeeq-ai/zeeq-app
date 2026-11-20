namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Loads completed review history for the same review group to provide
/// agents with prior-finding context.
/// </summary>
public interface ICodeReviewPreviousReviewStore
{
    /// <summary>
    /// Loads up to <paramref name="maxRecords"/> completed review outputs
    /// for the same repo/PR/group, excluding <paramref name="excludeReviewId"/>.
    /// Returns parsed previous reviews ordered newest-first.
    /// </summary>
    Task<IReadOnlyList<CodeReviewPreviousReview>> LoadAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        string reviewGroupId,
        string excludeReviewId,
        int maxRecords = 3,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Loads previous reviews for MCP runs only when a review group is supplied.
    /// A session is attribution, not a chain: sessions can span unrelated work.
    /// The record limit applies before expanding all reviewer facets.
    /// </summary>
    Task<IReadOnlyList<CodeReviewPreviousReview>> LoadForAgentAsync(
        string organizationId,
        string? agentSessionId,
        string? reviewGroupId,
        string excludeReviewId,
        int maxRecords = 3,
        CancellationToken cancellationToken = default
    );
}
