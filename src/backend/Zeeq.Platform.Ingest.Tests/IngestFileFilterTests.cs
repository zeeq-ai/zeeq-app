using Zeeq.Core.Documents.Dispatch;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// Tests for <see cref="IngestFileFilter"/>, in particular the
/// <c>Microsoft.Extensions.FileSystemGlobbing</c>-backed <c>**</c> semantics
/// that replaced the old ad hoc regex dialect (that dialect treated <c>**</c>
/// as a plain <c>*</c>, so a pattern like <c>dir/**/*.md</c> required an extra
/// path segment and never matched a file directly in <c>dir/</c> — the exact
/// footgun hit during Phase 1.8 acceptance testing against a real repo).
/// </summary>
public sealed class IngestFileFilterTests
{
    [Test]
    public async Task IsIncluded_NonMarkdownExtension_IsExcludedRegardlessOfFilter()
    {
        var included = IngestFileFilter.IsIncluded("readme.txt", EffectiveFilter.Empty);

        await Assert.That(included).IsFalse();
    }

    [Test]
    public async Task IsIncluded_EmptyIncludeGlobs_MatchesEverythingMarkdown()
    {
        var included = IngestFileFilter.IsIncluded("any/nested/path/doc.md", EffectiveFilter.Empty);

        await Assert.That(included).IsTrue();
    }

    [Test]
    public async Task IsIncluded_RecursiveGlob_MatchesFileDirectlyInIntermediateDirectory()
    {
        // The regression this fix targets: "dir/**/*.md" must match a file
        // directly in "dir/", with zero intervening path segments.
        var filter = new EffectiveFilter([".agents/context/**/*.md"], []);

        var included = IngestFileFilter.IsIncluded(".agents/context/doc.md", filter);

        await Assert.That(included).IsTrue();
    }

    [Test]
    public async Task IsIncluded_RecursiveGlob_AlsoMatchesDeeperNesting()
    {
        var filter = new EffectiveFilter([".agents/context/**/*.md"], []);

        var included = IngestFileFilter.IsIncluded(
            ".agents/context/nested/deep/doc.md",
            filter
        );

        await Assert.That(included).IsTrue();
    }

    [Test]
    public async Task IsIncluded_ExcludeWinsOverInclude()
    {
        var filter = new EffectiveFilter(["docs/**/*.md"], ["docs/draft/**/*.md"]);

        var included = IngestFileFilter.IsIncluded("docs/draft/wip.md", filter);

        await Assert.That(included).IsFalse();
    }

    [Test]
    public async Task IsIncluded_PathOutsideIncludeGlob_IsExcluded()
    {
        var filter = new EffectiveFilter(["docs/**/*.md"], []);

        var included = IngestFileFilter.IsIncluded("other/readme.md", filter);

        await Assert.That(included).IsFalse();
    }
}
