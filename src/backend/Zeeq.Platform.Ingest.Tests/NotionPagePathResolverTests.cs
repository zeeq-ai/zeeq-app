using NSubstitute;
using Zeeq.Integrations.Notion;

namespace Zeeq.Platform.Ingest.Tests;

/// <summary>
/// dotnet run --project src/backend/Zeeq.Platform.Ingest.Tests --output detailed --disable-logo --treenode-filter "/*/*/NotionPagePathResolverTests/*"
/// </summary>
public sealed class NotionPagePathResolverTests
{
    private readonly IZeeqNotionClient _client = Substitute.For<IZeeqNotionClient>();

    [Test]
    public async Task ResolveAsync_SharedAncestor_FetchesAncestorOnce()
    {
        ArrangePages(
            new NotionPage("child-1", "First", new NotionPageParent(false, "parent")),
            new NotionPage("child-2", "Second", new NotionPageParent(false, "parent")),
            new NotionPage("parent", "Team", new NotionPageParent(true, null))
        );
        var resolver = new NotionPagePathResolver(_client);

        var first = await resolver.ResolveAsync("child-1", CancellationToken.None);
        var second = await resolver.ResolveAsync("child-2", CancellationToken.None);

        await Assert.That(first!.Path).IsEqualTo("/team/first.md");
        await Assert.That(second!.Path).IsEqualTo("/team/second.md");
        await _client.Received(1).GetPageAsync("parent", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_SeededLeaf_DoesNotRefetchLeafMetadata()
    {
        ArrangePages(new NotionPage("parent", "Root", new NotionPageParent(true, null)));
        var resolver = new NotionPagePathResolver(_client);
        resolver.Seed(
            new NotionPageSummary(
                "child",
                "Guide",
                new NotionPageParent(false, "parent"),
                DateTimeOffset.UtcNow
            )
        );

        var resolved = await resolver.ResolveAsync("child", CancellationToken.None);

        await Assert.That(resolved!.Path).IsEqualTo("/root/guide.md");
        await _client.DidNotReceive().GetPageAsync("child", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_TitleSeparators_AreKeptInsideOneSegment()
    {
        ArrangePages(
            new NotionPage("page", "  Product / API\\Guide  ", new NotionPageParent(true, null))
        );
        var resolver = new NotionPagePathResolver(_client);

        var resolved = await resolver.ResolveAsync("page", CancellationToken.None);

        await Assert.That(resolved!.Path).IsEqualTo("/product-api-guide.md");
    }

    [Test]
    public async Task ResolveAsync_CyclicParentChain_Throws()
    {
        ArrangePages(
            new NotionPage("page-1", "One", new NotionPageParent(false, "page-2")),
            new NotionPage("page-2", "Two", new NotionPageParent(false, "page-1"))
        );
        var resolver = new NotionPagePathResolver(_client);

        async Task Act() => await resolver.ResolveAsync("page-1", CancellationToken.None);

        await Assert.That(Act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ResolveAsync_ExcessiveDepth_Throws()
    {
        _client
            .GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(0);
                var index = int.Parse(id.AsSpan("page-".Length));
                return new NotionPage(id, id, new NotionPageParent(false, $"page-{index + 1}"));
            });
        var resolver = new NotionPagePathResolver(_client);

        async Task Act() => await resolver.ResolveAsync("page-0", CancellationToken.None);

        await Assert.That(Act).Throws<InvalidOperationException>();
    }

    private void ArrangePages(params NotionPage[] pages)
    {
        var byId = pages.ToDictionary(page => page.PageId, StringComparer.Ordinal);
        _client
            .GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => byId.GetValueOrDefault(call.ArgAt<string>(0)));
    }
}
