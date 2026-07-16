namespace Zeeq.Core.Models;

/// <summary>
/// One repository-relative file matching rule for code-review filters.
/// </summary>
public sealed class CodeReviewFileMatchCriteria
{
    /// <summary>How <see cref="Pattern"/> should be interpreted.</summary>
    public CodeReviewFileNameMatchType MatchType { get; set; }

    /// <summary>Repository-relative path, prefix, extension, or glob pattern.</summary>
    public string Pattern { get; set; } = string.Empty;
}
