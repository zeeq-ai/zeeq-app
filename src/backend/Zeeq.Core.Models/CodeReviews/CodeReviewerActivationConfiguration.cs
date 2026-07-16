namespace Zeeq.Core.Models;

/// <summary>
/// File activation rules for one persisted code reviewer agent.
/// </summary>
/// <remarks>
/// Activation filters decide whether an agent participates in a review after
/// repository file filters have selected the in-scope file set. Excludes always
/// win over includes.
/// </remarks>
public sealed class CodeReviewerActivationConfiguration
{
    /// <summary>Default activation that lets the agent review all in-scope files.</summary>
    public static CodeReviewerActivationConfiguration Empty { get; } = new();

    /// <summary>Optional allowlist rules. Empty means the agent can review all in-scope files.</summary>
    public List<CodeReviewFileMatchCriteria> IncludedFiles { get; set; } = [];

    /// <summary>Deny rules that always win over includes.</summary>
    public List<CodeReviewFileMatchCriteria> ExcludedFiles { get; set; } = [];
}
