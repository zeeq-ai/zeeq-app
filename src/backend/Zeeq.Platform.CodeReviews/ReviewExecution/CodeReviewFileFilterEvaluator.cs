using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Applies repository-level file filters to source snapshots.
/// </summary>
/// <remarks>
/// Repository filters decide which files are in the shared review context. Agent activation filters are evaluated
/// later when choosing reviewers; keeping this helper repository-scoped prevents the two concepts from drifting.
/// </remarks>
public static class CodeReviewFileFilterEvaluator
{
    /// <summary>
    /// Splits source files into in-scope and out-of-scope sets with excludes winning over includes.
    /// </summary>
    public static CodeReviewFileScope Apply(
        IReadOnlyList<CodeReviewFileSnapshot> files,
        CodeReviewFileFilter? filter
    )
    {
        var effectiveFilter = filter ?? CodeReviewFileFilter.Empty;
        var inScope = new List<CodeReviewFileSnapshot>(files.Count);
        var outOfScope = new List<CodeReviewFileSnapshot>();

        foreach (var file in files)
        {
            if (IsIncluded(file, effectiveFilter))
            {
                inScope.Add(file);
            }
            else
            {
                outOfScope.Add(file);
            }
        }

        return new CodeReviewFileScope(inScope, outOfScope);
    }

    private static bool IsIncluded(CodeReviewFileSnapshot file, CodeReviewFileFilter filter)
    {
        var included =
            filter.IncludedFiles.Count == 0
            || filter.IncludedFiles.Any(criteria =>
                CodeReviewFilePatternMatcher.Matches(file, criteria)
            );
        if (!included)
        {
            return false;
        }

        return !filter.ExcludedFiles.Any(criteria =>
            CodeReviewFilePatternMatcher.Matches(file, criteria)
        );
    }
}

/// <summary>
/// Result of applying repository-level file filters to a source snapshot.
/// </summary>
public sealed record CodeReviewFileScope(
    IReadOnlyList<CodeReviewFileSnapshot> InScopeFiles,
    IReadOnlyList<CodeReviewFileSnapshot> OutOfScopeFiles
);
