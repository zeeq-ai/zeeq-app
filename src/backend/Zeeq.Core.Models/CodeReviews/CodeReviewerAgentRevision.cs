namespace Zeeq.Core.Models;

/// <summary>
/// Immutable snapshot of one reviewer agent configuration.
/// </summary>
/// <remarks>
/// Revisions are content-addressed: identical configuration content does not create a
/// second row. A revision is never updated after creation, and a run manifest keeps its
/// own copy of the content so later edits cannot relabel history. Tests and history
/// selection never restore or deploy a saved revision.
/// </remarks>
public sealed class CodeReviewerAgentRevision : DomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Optional team context inherited from the repository mapping.</summary>
    public string? TeamId { get; init; }

    /// <summary>Repository mapping the agent belongs to.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>Reviewer agent this revision snapshots.</summary>
    public required string AgentId { get; init; }

    /// <summary>SHA-256 of the canonical configuration content, used for deduplication.</summary>
    /// <remarks>Derived content identity. The store computes it; callers must not invent it.</remarks>
    public required string ContentHash { get; set; }

    /// <summary>Optional operator label. Defaults to an empty string and is never an identity.</summary>
    public required string Label { get; set; }

    /// <summary>Full reviewer configuration captured by this revision.</summary>
    public required CodeReviewerAgentRevisionConfiguration Configuration { get; init; }

    /// <summary>Authenticated user that produced this revision.</summary>
    public required string CreatedByUserId { get; init; }
}

/// <summary>
/// Reviewer configuration content captured by a saved revision.
/// </summary>
/// <param name="DisplayName">Reviewer name at capture time.</param>
/// <param name="ReviewFacet">Review facet at capture time.</param>
/// <param name="ModelTier">Semantic model tier at capture time.</param>
/// <param name="Prompt">Reviewer instructions at capture time.</param>
/// <param name="Enabled">Whether the agent was enabled at capture time.</param>
/// <param name="ActivationConfiguration">File activation rules at capture time.</param>
public sealed record CodeReviewerAgentRevisionConfiguration(
    string DisplayName,
    string ReviewFacet,
    CodeReviewModelTier ModelTier,
    string Prompt,
    bool Enabled,
    CodeReviewerActivationConfiguration ActivationConfiguration
);
