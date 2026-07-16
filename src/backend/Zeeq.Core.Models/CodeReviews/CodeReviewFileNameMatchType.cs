namespace Zeeq.Core.Models;

/// <summary>
/// Match operation used by a code-review file filter criterion.
/// </summary>
public enum CodeReviewFileNameMatchType
{
    /// <summary>Match an exact repository-relative path.</summary>
    ExactPath,

    /// <summary>Match a path prefix such as a directory.</summary>
    PathPrefix,

    /// <summary>Match a file extension such as <c>.cs</c>.</summary>
    Extension,

    /// <summary>Match a simple glob-like pattern.</summary>
    Glob,
}
