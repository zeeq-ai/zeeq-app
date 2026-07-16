using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Shared path matcher for code-review file-scope and agent-activation rules.
/// </summary>
/// <remarks>
/// Repository filters and agent activation filters are separate domain concepts,
/// but both use the same path criterion shape. Keep the primitive matcher here
/// so include/exclude semantics cannot drift as the runner evolves.
/// <para>
/// <see cref="CodeReviewFileNameMatchType.Glob"/> matches via
/// <c>Microsoft.Extensions.FileSystemGlobbing</c> — the same library
/// <c>IngestFileFilter</c> uses, so both subsystems share one real glob
/// dialect instead of two independent ad hoc regex-based ones. A
/// <see cref="Matcher"/> is built once per distinct pattern string and
/// cached, since the same pattern recurs across every file a criterion is
/// evaluated against. The cache is a size-limited <see cref="MemoryCache"/>
/// (not an unbounded dictionary) — patterns are admin-configured per repo, so
/// cardinality is normally small, but a long-running process across many
/// repos with churning criteria should still evict rather than retain every
/// distinct pattern ever seen. Kept as a plain static instance (not DI
/// <c>IMemoryCache</c>/<c>HybridCache</c>) deliberately — this class and its
/// caller <c>CodeReviewFileFilterEvaluator</c> are both static utilities by
/// design; injecting a cache dependency here would force both into
/// instance/DI-resolved shapes for a Minor optimization.
/// <b>Behavior change vs. the old regex dialect</b> — see <c>IngestFileFilter</c>'s
/// matching remark for the full explanation: <c>**</c> is now truly
/// recursive (widening), and a bare <c>*</c> no longer crosses directory
/// boundaries (narrowing) — any existing <see cref="CodeReviewFileNameMatchType.Glob"/>
/// criterion relying on the old cross-directory <c>*</c> quirk will now
/// match fewer files.
/// </para>
/// </remarks>
internal static class CodeReviewFilePatternMatcher
{
    private const int GlobMatcherCacheSizeLimit = 500;

    private static readonly MemoryCache GlobMatcherCache = new(
        new MemoryCacheOptions { SizeLimit = GlobMatcherCacheSizeLimit }
    );

    /// <summary>
    /// Returns whether a file's current or previous path matches the criterion.
    /// </summary>
    public static bool Matches(CodeReviewFileSnapshot file, CodeReviewFileMatchCriteria criteria)
    {
        var pattern = NormalizePath(criteria.Pattern);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        return PathMatches(file.Path, pattern, criteria.MatchType)
            || (
                file.PreviousPath is { Length: > 0 } previousPath
                && PathMatches(previousPath, pattern, criteria.MatchType)
            );
    }

    private static bool PathMatches(
        string path,
        string normalizedPattern,
        CodeReviewFileNameMatchType matchType
    )
    {
        var normalizedPath = NormalizePath(path);

        return matchType switch
        {
            CodeReviewFileNameMatchType.ExactPath => string.Equals(
                normalizedPath,
                normalizedPattern,
                StringComparison.OrdinalIgnoreCase
            ),
            CodeReviewFileNameMatchType.PathPrefix => normalizedPath.StartsWith(
                normalizedPattern.TrimEnd('/') + "/",
                StringComparison.OrdinalIgnoreCase
            )
                || string.Equals(
                    normalizedPath,
                    normalizedPattern.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase
                ),
            CodeReviewFileNameMatchType.Extension => string.Equals(
                Path.GetExtension(normalizedPath),
                normalizedPattern.StartsWith('.') ? normalizedPattern : "." + normalizedPattern,
                StringComparison.OrdinalIgnoreCase
            ),
            CodeReviewFileNameMatchType.Glob => GlobMatcher(normalizedPattern)
                .Match(normalizedPath)
                .HasMatches,
            _ => false,
        };
    }

    private static Matcher GlobMatcher(string normalizedPattern) =>
        GlobMatcherCache.GetOrCreate(
            normalizedPattern,
            entry =>
            {
                entry.Size = 1;
                return new Matcher(StringComparison.OrdinalIgnoreCase).AddInclude(
                    normalizedPattern
                );
            }
        )!;

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');
}
