using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Data.Postgres.CodeReviews;

/// <summary>
/// Postgres-backed store for persisted reviewer agents.
/// </summary>
internal sealed class PostgresCodeReviewerAgentStore(PostgresDbContext db) : ICodeReviewerAgentStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewerAgent>> ListForRepositoryAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeReviewerAgents.TagWithOperationCallSite("code_reviewer_agent.list_for_repository")
            .Where(agent =>
                agent.OrganizationId == organizationId
                && agent.RepositoryId == repositoryId
                && agent.DisabledAtUtc == null
            )
            .OrderBy(agent => agent.DisplayName)
            .ThenBy(agent => agent.Id)
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodeReviewerAgent>> ListEnabledForRepositoryAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    ) =>
        await db
            .CodeReviewerAgents.TagWithOperationCallSite(
                "code_reviewer_agent.list_enabled_for_repository"
            )
            .Where(agent =>
                agent.OrganizationId == organizationId
                && agent.RepositoryId == repositoryId
                && agent.DisabledAtUtc == null
                && agent.Enabled
            )
            .OrderBy(agent => agent.DisplayName)
            .ThenBy(agent => agent.Id)
            .ToArrayAsync(cancellationToken);

    /// <inheritdoc />
    public Task<CodeReviewerAgent?> FindAsync(
        string organizationId,
        string agentId,
        CancellationToken cancellationToken
    ) =>
        db
            .CodeReviewerAgents.TagWithOperationCallSite("code_reviewer_agent.find")
            .FirstOrDefaultAsync(
                agent =>
                    agent.OrganizationId == organizationId
                    && agent.Id == agentId
                    && agent.DisabledAtUtc == null,
                cancellationToken
            );

    /// <inheritdoc />
    public async Task<CodeReviewerAgent> AddAsync(
        CodeReviewerAgent agent,
        CancellationToken cancellationToken
    )
    {
        db.CodeReviewerAgents.Add(agent);
        await db.SaveChangesAsync(cancellationToken);

        return agent;
    }

    /// <inheritdoc />
    public async Task<CodeReviewerAgent> UpdateAsync(
        CodeReviewerAgent agent,
        CancellationToken cancellationToken
    )
    {
        var existing = await FindAsync(agent.OrganizationId, agent.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Code reviewer agent {agent.Id} was not found."
            );

        existing.TeamId = agent.TeamId;
        existing.RepositoryId = agent.RepositoryId;
        existing.DisplayName = agent.DisplayName;
        existing.ReviewFacet = agent.ReviewFacet;
        existing.ModelTier = agent.ModelTier;
        existing.Prompt = agent.Prompt;
        existing.Enabled = agent.Enabled;
        existing.ActivationConfiguration = agent.ActivationConfiguration;
        existing.UpdatedAtUtc = agent.UpdatedAtUtc;

        await db.SaveChangesAsync(cancellationToken);

        return existing;
    }

    /// <inheritdoc />
    public async Task<CodeReviewerAgent> SaveWithRevisionAsync(
        CodeReviewerAgent agent,
        bool create,
        string createdByUserId,
        string? label,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await ScopedStoreTransaction.BeginAsync(
            db,
            cancellationToken
        );
        CodeReviewerAgent saved;
        if (create)
        {
            saved = agent;
            db.CodeReviewerAgents.Add(saved);
        }
        else
        {
            // Serializes concurrent configuration writes and preserves the actual predecessor,
            // even when a handler loaded the agent before another request saved it.
            await db
                .Database.SqlQuery<int>(
                    $"""
                    SELECT 1 AS "Value" FROM code_reviewer_agents
                    WHERE organization_id = {agent.OrganizationId} AND id = {agent.Id}
                    FOR UPDATE
                    """
                )
                .ToListAsync(cancellationToken);
            saved = await FindAsync(agent.OrganizationId, agent.Id, cancellationToken)
                ?? throw new InvalidOperationException("The reviewer agent was not found.");
            await db.Entry(saved).ReloadAsync(cancellationToken);
            if (saved.DisabledAtUtc is not null || saved.RepositoryId != agent.RepositoryId)
                throw new InvalidOperationException("The reviewer agent is no longer available.");
            await StageRevisionAsync(
                saved,
                createdByUserId,
                string.Empty,
                agent.UpdatedAtUtc,
                cancellationToken
            );
            saved.TeamId = agent.TeamId;
            saved.DisplayName = agent.DisplayName;
            saved.ReviewFacet = agent.ReviewFacet;
            saved.ModelTier = agent.ModelTier;
            saved.Prompt = agent.Prompt;
            saved.Enabled = agent.Enabled;
            saved.ActivationConfiguration = agent.ActivationConfiguration;
            saved.UpdatedAtUtc = agent.UpdatedAtUtc;
        }
        await StageRevisionAsync(
            saved,
            createdByUserId,
            label ?? string.Empty,
            agent.UpdatedAtUtc,
            cancellationToken
        );
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    private async Task StageRevisionAsync(
        CodeReviewerAgent agent,
        string userId,
        string label,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        var configuration = CodeReviewerAgentRevisionContent.Capture(agent);
        var hash = CodeReviewerAgentRevisionContent.ComputeHash(configuration);
        if (
            db.CodeReviewerAgentRevisions.Local.Any(x =>
                x.OrganizationId == agent.OrganizationId
                && x.AgentId == agent.Id
                && x.ContentHash == hash
            )
            || await db.CodeReviewerAgentRevisions.AnyAsync(
                x =>
                    x.OrganizationId == agent.OrganizationId
                    && x.AgentId == agent.Id
                    && x.ContentHash == hash,
                cancellationToken
            )
        )
            return;
        db.CodeReviewerAgentRevisions.Add(
            new CodeReviewerAgentRevision
            {
                Id = $"crr_{Guid.CreateVersion7():N}",
                OrganizationId = agent.OrganizationId,
                TeamId = agent.TeamId,
                RepositoryId = agent.RepositoryId,
                AgentId = agent.Id,
                ContentHash = hash,
                Label = label.Trim(),
                Configuration = configuration,
                CreatedByUserId = userId,
                CreatedAtUtc = now,
            }
        );
    }

    /// <inheritdoc />
    public async Task<bool> DisableAsync(
        string organizationId,
        string agentId,
        DateTimeOffset disabledAtUtc,
        CancellationToken cancellationToken
    )
    {
        var existing = await FindAsync(organizationId, agentId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        existing.Enabled = false;
        existing.DisabledAtUtc = disabledAtUtc;
        existing.UpdatedAtUtc = disabledAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
