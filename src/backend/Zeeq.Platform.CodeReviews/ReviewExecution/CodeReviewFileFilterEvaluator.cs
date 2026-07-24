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
/// always wins; (2) a non-<see cref="CodeReviewFileNameMatchType.Extension"/> match (<c>ExactPath</c>,
/// <c>PathPrefix</c>, or <c>Glob</c>) in the repository's own <see cref="CodeReviewFileFilter.IncludedFiles"/>
/// allowlist — overrides the baseline exclusions below; (3) the baseline exclusions themselves —
/// <see cref="CodeReviewDefaultFileExclusions"/> (lockfiles, build output, vendored dependencies, generated
/// code, editor/OS noise) plus any file whose <see cref="CodeReviewFileSnapshot.MutationState"/> is
/// <see cref="CodeReviewFileMutationState.Binary"/> — excluded for every repository with no configuration
/// required; (4) the repository's own <see cref="CodeReviewFileFilter.IncludedFiles"/> acting as a plain
/// allowlist when non-empty.
/// </para>
/// <para>
/// A bare <see cref="CodeReviewFileNameMatchType.Extension"/> include (e.g. "all <c>.json</c> files", used by
/// several front-end language presets to scope review to source file types) deliberately cannot override the
/// baseline — it is incidental to any specific lockfile that happens to share the extension
/// (<c>package-lock.json</c>, for instance), not an explicit opt-in to reviewing it. A repo that genuinely wants
/// a specific generated/lockfile/binary path reviewed needs a targeted <c>ExactPath</c>, <c>PathPrefix</c>, or
/// <c>Glob</c> include rule instead.
/// </para>
/// <para>
/// The <see cref="CodeReviewFileMutationState.Binary"/> check is a content-based catch-all alongside the
/// extension-based <see cref="CodeReviewDefaultFileExclusions"/> list: a diff/PR source can mark any file
/// binary regardless of its extension (an unlisted extension like <c>.webp</c> or <c>.wasm</c>, or an
/// extensionless binary), and that file's patch text is empty or a placeholder either way — never useful to a
/// reviewer agent.
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
        // lockfile diffs reviewed adds an explicit include rule for that pattern. A bare
        // Extension match doesn't count: it's a blanket "all .json files" style rule, not
        // an explicit opt-in to the specific lockfile that happens to share the extension.
        var overridesDefaultExclusion = filter.IncludedFiles.Any(criteria =>
            criteria.MatchType != CodeReviewFileNameMatchType.Extension
            && CodeReviewFilePatternMatcher.Matches(file, criteria)
        );
        if (!overridesDefaultExclusion && MatchesDefaultExclusion(file))
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
        file.MutationState == CodeReviewFileMutationState.Binary
        || CodeReviewDefaultFileExclusions.Criteria.Any(criteria =>
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
