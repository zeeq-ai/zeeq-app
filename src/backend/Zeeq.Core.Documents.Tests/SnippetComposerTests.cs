using Zeeq.Core.Common;
using Zeeq.Core.Documents.Parsing;
using Zeeq.Core.Documents.Snippets;

namespace Zeeq.Core.Documents.Tests;

/// <summary>
/// Unit tests for <see cref="SnippetComposer"/> — the pure mapping from a parsed markdown document
/// to composed snippet rows (payload construction, hashing, ordinals, and caps).
/// </summary>
public sealed class SnippetComposerTests
{
    private static readonly SnippetIndexingSettings Settings = new();

    private static ParsedMarkdown Doc(
        string title = "Doc Title",
        IReadOnlyList<Section>? sections = null,
        IReadOnlyList<Snippet>? snippets = null
    ) =>
        new(
            Title: title,
            Keywords: [],
            Headings: [],
            Content: string.Empty,
            FrontMatter: string.Empty,
            Sections: sections ?? [],
            Snippets: snippets ?? []
        );

    private static Section Section(string body, string header = "Intro", string path = "Intro") =>
        new(Header: header, HeadingPath: path, Body: body);

    private static Snippet Code(
        string content,
        string header = "Usage",
        string path = "Guide > Usage",
        string preceding = "Example:",
        string language = "cs",
        string tag = "example"
    ) => new(header, path, preceding, language, content, tag);

    [Test]
    public async Task Compose_SectionBelowMinChars_IsSkipped()
    {
        var shortBody = new string('a', Settings.MinSectionChars - 1);

        var composed = SnippetComposer.Compose(Doc(sections: [Section(shortBody)]), Settings);

        await Assert.That(composed).IsEmpty();
    }

    [Test]
    public async Task Compose_SectionAtMinChars_IsIncluded()
    {
        var body = new string('a', Settings.MinSectionChars);

        var composed = SnippetComposer.Compose(Doc(sections: [Section(body)]), Settings);

        await Assert.That(composed).Count().IsEqualTo(1);
        await Assert.That(composed[0].Kind).IsEqualTo(SnippetKind.Section);
    }

    [Test]
    public async Task Compose_CodePayload_IncludesPrecedingAndFenceMetadata()
    {
        var composed = SnippetComposer.Compose(
            Doc(
                snippets:
                [
                    Code(
                        content: "var x = ComputeValue();",
                        preceding: "Call the helper:",
                        tag: "sample"
                    ),
                ]
            ),
            Settings
        );

        await Assert.That(composed).Count().IsEqualTo(1);
        var payload = composed[0].EmbeddingPayload;
        await Assert.That(payload).Contains("Call the helper:");
        await Assert.That(payload).Contains("sample");
        await Assert.That(payload).Contains("(cs)");
        await Assert.That(payload).Contains("var x = ComputeValue();");
    }

    [Test]
    public async Task Compose_CodeSnippet_ExtractsIdentifiers()
    {
        var composed = SnippetComposer.Compose(
            Doc(
                snippets:
                [
                    Code(
                        content: "var result = ComputeRepositoryValue(repositoryUserName);"
                    ),
                ]
            ),
            Settings
        );

        await Assert.That(composed[0].Identifiers).Contains("computerepositoryvalue");
        await Assert.That(composed[0].Identifiers).Contains("repositoryusername");
    }

    [Test]
    public async Task Compose_DuplicateIdenticalFences_GetDistinctOrdinals()
    {
        var duplicate = Code(content: "DoThing();");

        var composed = SnippetComposer.Compose(Doc(snippets: [duplicate, duplicate]), Settings);

        await Assert.That(composed).Count().IsEqualTo(2);
        await Assert.That(composed[0].ContentHash).IsEqualTo(composed[1].ContentHash);
        await Assert.That(composed[0].Ordinal).IsEqualTo(0);
        await Assert.That(composed[1].Ordinal).IsEqualTo(1);
    }

    [Test]
    public async Task Compose_PerDocumentCap_IsEnforced()
    {
        var settings = Settings with { MaxSnippetsPerDocument = 3, MinSectionChars = 1 };
        var sections = Enumerable
            .Range(0, 10)
            .Select(i =>
                Section(
                    body: $"section body number {i} with enough text",
                    header: $"H{i}",
                    path: $"H{i}"
                )
            )
            .ToArray();

        var composed = SnippetComposer.Compose(Doc(sections: sections), settings);

        await Assert.That(composed).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Compose_Hash_IsStableAcrossRuns()
    {
        var doc = Doc(snippets: [Code(content: "Stable();")]);

        var first = SnippetComposer.Compose(doc, Settings);
        var second = SnippetComposer.Compose(doc, Settings);

        await Assert.That(first[0].ContentHash).IsEqualTo(second[0].ContentHash);
    }

    [Test]
    public async Task Compose_Hash_ChangesWhenHeadingPathChanges()
    {
        var body = new string('z', Settings.MinSectionChars);
        var docA = Doc(sections: [Section(body, path: "Guide > A")]);
        var docB = Doc(sections: [Section(body, path: "Guide > B")]);

        var a = SnippetComposer.Compose(docA, Settings);
        var b = SnippetComposer.Compose(docB, Settings);

        await Assert.That(a[0].ContentHash).IsNotEqualTo(b[0].ContentHash);
    }

    [Test]
    public async Task Compose_Hash_ChangesWhenTitleChanges()
    {
        var body = new string('z', Settings.MinSectionChars);
        var a = SnippetComposer.Compose(
            Doc(title: "Title One", sections: [Section(body)]),
            Settings
        );
        var b = SnippetComposer.Compose(
            Doc(title: "Title Two", sections: [Section(body)]),
            Settings
        );

        await Assert.That(a[0].ContentHash).IsNotEqualTo(b[0].ContentHash);
    }

    [Test]
    public async Task Compose_SectionSnippet_HasNoCodeMetadata()
    {
        var composed = SnippetComposer.Compose(
            Doc(sections: [Section(new string('a', Settings.MinSectionChars))]),
            Settings
        );

        await Assert.That(composed[0].Language).IsNull();
        await Assert.That(composed[0].Tag).IsNull();
        await Assert.That(composed[0].PrecedingText).IsNull();
        await Assert.That(composed[0].Identifiers).IsEmpty();
    }
}
