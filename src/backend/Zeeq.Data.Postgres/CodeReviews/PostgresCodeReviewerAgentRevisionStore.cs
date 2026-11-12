using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for immutable reviewer agent revisions.
/// </summary>
internal sealed class PostgresCodeReviewerAgentRevisionStore(PostgresDbContext db)
    : ICodeReviewerAgentRevisionStore
{
    /// <inheritdoc />
    public Task<CodeReviewerAgentRevision?> FindAsync(
        string organizationId,
        string agentId,
        string revisionId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewerAgentRevisions.TagWithOperationCallSite(
                "code_reviewer_agent_revision.find"
            )
            .FirstOrDefaultAsync(
                revision =>
                    revision.OrganizationId == organizationId
                    && revision.AgentId == agentId
                    && revision.Id == revisionId
                    && revision.DisabledAtUtc == null,
                cancellationToken
            );

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewerAgentRevision>> ListForAgentAsync(
        string organizationId,
        string agentId,
        string? cursorId,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        var page = Math.Clamp(pageSize, 1, 200);
        var query = db
            .CodeReviewerAgentRevisions.TagWithOperationCallSite(
                "code_reviewer_agent_revision.list_for_agent"
            )
            .Where(revision =>
                revision.OrganizationId == organizationId
                && revision.AgentId == agentId
                && revision.DisabledAtUtc == null
            );

        if (!string.IsNullOrWhiteSpace(cursorId))
        {
            // Revision ids are UUIDv7 values, so id ordering matches creation ordering and a
            // single-column seek cursor is stable for newest-first paging.
            query = query.Where(revision => revision.Id.CompareTo(cursorId) < 0);
        }

        return await query
            .OrderByDescending(revision => revision.Id)
            .Take(page)
            .ToArrayAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<CodeReviewerAgentRevision?> FindLatestAsync(
        string organizationId,
        string agentId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewerAgentRevisions.TagWithOperationCallSite(
                "code_reviewer_agent_revision.find_latest"
            )
            .Where(revision =>
                revision.OrganizationId == organizationId
                && revision.AgentId == agentId
                && revision.DisabledAtUtc == null
            )
            .OrderByDescending(revision => revision.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<CodeReviewerAgentRevision> AddAsync(
        CodeReviewerAgentRevision revision,
        CancellationToken cancellationToken
    )
    {
        // The store owns the content hash so a caller cannot create two identities for the
        // same configuration content.
        revision.ContentHash = CodeReviewerAgentRevisionContent.ComputeHash(revision.Configuration);

        var existing = await db
            .CodeReviewerAgentRevisions.TagWithOperationCallSite(
                "code_reviewer_agent_revision.find_by_content"
            )
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.OrganizationId == revision.OrganizationId
                    && candidate.RepositoryId == revision.RepositoryId
                    && candidate.AgentId == revision.AgentId
                    && candidate.ContentHash == revision.ContentHash,
                cancellationToken
            );

        if (existing is not null)
        {
            return existing;
        }

        db.CodeReviewerAgentRevisions.Add(revision);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return revision;
        }
        catch (DbUpdateException exception)
            when (PostgresConstraintViolation.IsUniqueViolation(
                    exception,
                    "ix_code_reviewer_agent_revisions_organization_id_repository_id_age"
                )
            )
        {
            // Only the content-addressing unique index is an expected race. Any other database
            // failure must surface instead of being mistaken for an idempotent duplicate.
            db.Entry(revision).State = EntityState.Detached;

            var winner = await db
                .CodeReviewerAgentRevisions.TagWithOperationCallSite(
                    "code_reviewer_agent_revision.find_by_content"
                )
                .FirstOrDefaultAsync(
                    candidate =>
                        candidate.OrganizationId == revision.OrganizationId
                        && candidate.RepositoryId == revision.RepositoryId
                        && candidate.AgentId == revision.AgentId
                        && candidate.ContentHash == revision.ContentHash,
                    cancellationToken
                );

            return winner
                ?? throw new InvalidOperationException(
                    "A reviewer agent revision content race could not be resolved."
                );
        }
    }

    /// <inheritdoc />
    public async Task<CodeReviewerAgentRevision> EnsureInitialSnapshotAsync(
        CodeReviewerAgent agent,
        string createdByUserId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    )
    {
        var configuration = CodeReviewerAgentRevisionContent.Capture(agent);
        var contentHash = CodeReviewerAgentRevisionContent.ComputeHash(configuration);

        // Match the current CONTENT, not merely the newest row. A revert (A -> B -> A) leaves
        // B as the newest revision while the live agent is A again; reusing "latest" would hand
        // back a stale snapshot.
        var current = await db
            .CodeReviewerAgentRevisions.TagWithOperationCallSite(
                "code_reviewer_agent_revision.find_by_current_hash"
            )
            .AsNoTracking()
            .FirstOrDefaultAsync(
                revision =>
                    revision.OrganizationId == agent.OrganizationId
                    && revision.RepositoryId == agent.RepositoryId
                    && revision.AgentId == agent.Id
                    && revision.ContentHash == contentHash,
                cancellationToken
            );

        if (current is not null)
        {
            return current;
        }

        return await AddAsync(
            new CodeReviewerAgentRevision
            {
                Id = $"crr_{Guid.CreateVersion7():N}",
                OrganizationId = agent.OrganizationId,
                TeamId = agent.TeamId,
                RepositoryId = agent.RepositoryId,
                AgentId = agent.Id,
                ContentHash = string.Empty,
                Label = string.Empty,
                Configuration = CodeReviewerAgentRevisionContent.Capture(agent),
                CreatedByUserId = createdByUserId,
                CreatedAtUtc = nowUtc,
            },
            cancellationToken
        );
    }
}
