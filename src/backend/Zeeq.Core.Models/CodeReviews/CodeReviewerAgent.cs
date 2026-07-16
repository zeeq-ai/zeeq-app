namespace Zeeq.Core.Models;

/// <summary>
/// Repository-scoped reviewer agent configured by a Zeeq operator.
/// </summary>
/// <remarks>
/// Agents are persisted configuration, not runtime LLM clients. Runtime
/// resolution converts this row into a per-run reviewer using the current
/// organization LLM settings or system defaults.
/// </remarks>
public sealed class CodeReviewerAgent : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>Optional team context for this agent.</summary>
    public string? TeamId { get; set; }

    /// <summary>Repository mapping this agent belongs to.</summary>
    public required string RepositoryId { get; set; }

    /// <summary>Human-readable agent name shown in management screens and comments.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Review facet label, for example <c>Security</c> or <c>Performance</c>.</summary>
    public required string ReviewFacet { get; set; }

    /// <summary>Zeeq model tier to resolve at run time.</summary>
    public required CodeReviewModelTier ModelTier { get; set; }

    /// <summary>Reviewer instructions appended to the shared review prompt.</summary>
    public required string Prompt { get; set; }

    /// <summary>Whether this persisted agent can participate in new review runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>File activation filters for this agent.</summary>
    public CodeReviewerActivationConfiguration ActivationConfiguration { get; set; } =
        CodeReviewerActivationConfiguration.Empty;
}
