using System.Runtime.CompilerServices;
using Zeeq.Core.Documents.Dispatch;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Decides which repository files are in scope for one ingest run.
/// </summary>
/// <remarks>
/// Two narrowing rules always apply, per spec §5.3: only Markdown-family
/// extensions are ever ingested, and the effective include/exclude globs
/// (union of subscribing libraries' filters for public sources, or one
/// library's filter for private sources) are applied on top. A repo with no
/// configured filter still only pulls the three Markdown extensions — this
/// mirrors the sparse-checkout narrowing the git layer will apply once cloning
/// is wired in; the runner enforces the same rule locally so it behaves
/// identically whether files came from a full checkout or the future sparse one.
/// <para>
/// Glob matching is <c>Microsoft.Extensions.FileSystemGlobbing</c> — the same
/// library <c>CodeReviewFilePatternMatcher</c> uses for its <c>Glob</c> match
/// type, so filter authors learn one real glob dialect across both
/// subsystems. One <see cref="Matcher"/> is built per <see cref="EffectiveFilter"/>
/// instance and cached (<see cref="ConditionalWeakTable{TKey,TValue}"/>) rather
/// than rebuilt per file — the same filter instance is reused across an
/// entire run's file walk.
/// </para>
/// <para>
/// <b>Behavior change vs. the old regex dialect this replaced</b> — flagged
/// here because it affects already-configured filter data, not just internal
/// implementation: (1) <c>**</c> is now truly recursive, including zero
/// intervening path segments (<c>dir/**/*.md</c> now matches <c>dir/file.md</c>,
/// not just <c>dir/sub/file.md</c>) — a widening, and the actual motivation
/// for this change (Phase 1.8 acceptance testing hit exactly this gap against
/// a real repo). (2) A bare <c>*</c> no longer crosses directory boundaries —
/// the old regex replaced every <c>*</c> with <c>.*</c>, so <c>dir/*.md</c>
/// used to also match <c>dir/sub/deep.md</c>; that was never a designed
/// feature, just an artifact of the string-replace implementation, and no
/// other glob-matching tool (gitignore, <c>.gitattributes</c>, this same
/// library) lets a bare <c>*</c> cross directories. Confirmed via direct
/// testing during this change; no currently-configured filter in this
/// codebase's data depended on it, but any external/production filter
/// authored to lean on the old cross-directory <c>*</c> quirk will now match
/// fewer files after this ships.
/// </para>
/// </remarks>
public static class IngestFileFilter
{
    private static readonly string[] MarkdownExtensions = [".md", ".mdc", ".mdx"];
    private static readonly ConditionalWeakTable<EffectiveFilter, Matcher> MatcherCache = new();

    /// <summary>
    /// Returns whether a repository-relative path should be ingested.
    /// </summary>
    /// <param name="relativePath">Forward- or backslash-separated path relative to the repository root.</param>
    /// <param name="filter">The effective include/exclude globs for this run.</param>
    public static bool IsIncluded(string relativePath, EffectiveFilter filter)
    {
        if (
            !MarkdownExtensions.Contains(
                Path.GetExtension(relativePath),
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        var matcher = MatcherCache.GetValue(filter, BuildMatcher);
        var normalized = Normalize(relativePath);

        return matcher.Match(normalized).HasMatches;
    }

    /// <summary>An empty <see cref="EffectiveFilter.IncludeGlobs"/> means "include everything."</summary>
    private static Matcher BuildMatcher(EffectiveFilter filter)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);

        if (filter.IncludeGlobs.Length == 0)
        {
            matcher.AddInclude("**/*");
        }
        else
        {
            matcher.AddIncludePatterns(filter.IncludeGlobs.Select(Normalize));
        }

        matcher.AddExcludePatterns(filter.ExcludeGlobs.Select(Normalize));

        return matcher;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/');
}
