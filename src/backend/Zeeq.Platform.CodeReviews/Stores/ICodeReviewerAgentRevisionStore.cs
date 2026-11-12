using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for immutable reviewer agent revision snapshots.
/// </summary>
/// <remarks>
/// Revisions are content-addressed per agent: writing identical configuration content
/// returns the existing row instead of creating noise. Rows are immutable after creation.
/// Nothing here deploys, restores or applies a revision to a live agent.
/// </remarks>
public interface ICodeReviewerAgentRevisionStore
{
    /// <summary>Finds one revision scoped to an organization and agent.</summary>
    Task<CodeReviewerAgentRevision?> FindAsync(
        string organizationId,
        string agentId,
        string revisionId,
        CancellationToken cancellationToken
    );

    /// <summary>Lists revisions for one agent, newest first, with seek pagination.</summary>
    Task<IReadOnlyList<CodeReviewerAgentRevision>> ListForAgentAsync(
        string organizationId,
        string agentId,
        string? cursorId,
        int pageSize,
        CancellationToken cancellationToken
    );

    /// <summary>Finds the newest revision for one agent.</summary>
    Task<CodeReviewerAgentRevision?> FindLatestAsync(
        string organizationId,
        string agentId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Adds a revision, or returns the existing revision with the same content hash.
    /// </summary>
    Task<CodeReviewerAgentRevision> AddAsync(
        CodeReviewerAgentRevision revision,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Writes an initial snapshot for an agent that has no revision yet.
    /// </summary>
    /// <remarks>
    /// Older agents get their snapshot when first viewed or tested. There is no batch
    /// backfill, and a no-op write never creates a duplicate revision.
    /// </remarks>
    Task<CodeReviewerAgentRevision> EnsureInitialSnapshotAsync(
        CodeReviewerAgent agent,
        string createdByUserId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    );
}
