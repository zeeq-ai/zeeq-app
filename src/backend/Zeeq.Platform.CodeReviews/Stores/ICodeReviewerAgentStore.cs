using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Store for persisted repository-scoped reviewer agent configuration.
/// </summary>
public interface ICodeReviewerAgentStore
{
    /// <summary>Lists non-deleted agents for a repository.</summary>
    Task<IReadOnlyList<CodeReviewerAgent>> ListForRepositoryAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    );

    /// <summary>Lists enabled, non-deleted agents for a repository.</summary>
    Task<IReadOnlyList<CodeReviewerAgent>> ListEnabledForRepositoryAsync(
        string organizationId,
        string repositoryId,
        CancellationToken cancellationToken
    );

    /// <summary>Finds one non-deleted agent by id inside an organization.</summary>
    Task<CodeReviewerAgent?> FindAsync(
        string organizationId,
        string agentId,
        CancellationToken cancellationToken
    );

    /// <summary>Adds a new reviewer agent.</summary>
    Task<CodeReviewerAgent> AddAsync(CodeReviewerAgent agent, CancellationToken cancellationToken);

    /// <summary>Updates a reviewer agent.</summary>
    Task<CodeReviewerAgent> UpdateAsync(
        CodeReviewerAgent agent,
        CancellationToken cancellationToken
    );

    /// <summary>Soft-disables a reviewer agent.</summary>
    Task<bool> DisableAsync(
        string organizationId,
        string agentId,
        DateTimeOffset disabledAtUtc,
        CancellationToken cancellationToken
    );
}
