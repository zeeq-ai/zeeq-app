namespace Zeeq.Core.Models;

/// <summary>
/// Include/exclude file filters for code-review source selection.
/// </summary>
/// <remarks>
/// Empty filters preserve the candidate set. Include filters act as an
/// allowlist when present, and exclude filters always win over includes.
/// Repository filters select files; agent activation filters select agents.
/// </remarks>
public sealed class CodeReviewFileFilter
{
    /// <summary>Empty filter that leaves all candidate files in scope.</summary>
    public static CodeReviewFileFilter Empty { get; } = new();

    /// <summary>Optional allowlist rules. Empty means all candidate files are allowed.</summary>
    public List<CodeReviewFileMatchCriteria> IncludedFiles { get; set; } = [];

    /// <summary>Deny rules that always win over includes.</summary>
    public List<CodeReviewFileMatchCriteria> ExcludedFiles { get; set; } = [];
}
