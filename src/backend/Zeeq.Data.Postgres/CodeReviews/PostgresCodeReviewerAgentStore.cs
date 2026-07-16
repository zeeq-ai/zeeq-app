using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.EntityFrameworkCore;

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
        var existing =
            await FindAsync(agent.OrganizationId, agent.Id, cancellationToken)
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
