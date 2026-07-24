using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Applies repository-level file filters to source snapshots.
/// </summary>
/// <remarks>
/// Repository filters decide which files are in the shared review context. Agent activation filters are evaluated
/// later when choosing reviewers; keeping this helper repository-scoped prevents the two concepts from drifting.
/// <para>
/// Precedence, highest to lowest: (1) the repository's own <see cref="CodeReviewFileFilter.ExcludedFiles"/> —
/// always wins; (2) an explicit match in the repository's own <see cref="CodeReviewFileFilter.IncludedFiles"/>
/// allowlist — overrides the baseline exclusions below; (3) <see cref="CodeReviewDefaultFileExclusions"/>, a
/// hardcoded baseline (lockfiles, build output, vendored dependencies, generated code, editor/OS noise) excluded
/// for every repository with no configuration required; (4) the repository's own
/// <see cref="CodeReviewFileFilter.IncludedFiles"/> acting as a plain allowlist when non-empty.
/// </para>
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
        var hasIncludeAllowlist = filter.IncludedFiles.Count > 0;
        var matchesIncludeAllowlist =
            hasIncludeAllowlist
            && filter.IncludedFiles.Any(criteria =>
                CodeReviewFilePatternMatcher.Matches(file, criteria)
            );

        // A repo-configured include match is the one way to pull a file back into scope
        // despite matching a baseline default exclusion — e.g. a repo that wants
        // lockfile diffs reviewed adds an explicit include rule for that pattern.
        if (!matchesIncludeAllowlist && MatchesDefaultExclusion(file))
        {
            return false;
        }

        if (hasIncludeAllowlist && !matchesIncludeAllowlist)
        {
            return false;
        }

        return !filter.ExcludedFiles.Any(criteria =>
            CodeReviewFilePatternMatcher.Matches(file, criteria)
        );
    }

    private static bool MatchesDefaultExclusion(CodeReviewFileSnapshot file) =>
        CodeReviewDefaultFileExclusions.Criteria.Any(criteria =>
            CodeReviewFilePatternMatcher.Matches(file, criteria)
        );
}

/// <summary>
/// Result of applying repository-level file filters to a source snapshot.
/// </summary>
public sealed record CodeReviewFileScope(
    IReadOnlyList<CodeReviewFileSnapshot> InScopeFiles,
    IReadOnlyList<CodeReviewFileSnapshot> OutOfScopeFiles
);
