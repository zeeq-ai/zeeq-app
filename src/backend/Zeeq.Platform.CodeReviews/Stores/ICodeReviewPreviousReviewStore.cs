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
    /// Loads previous reviews for agent (MCP) runs by session id and/or group id (either).
    /// Returns empty when both keys are null. Chains across resumed sessions where either
    /// identifier is known, so an agent that only knows the group id still finds prior context.
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
