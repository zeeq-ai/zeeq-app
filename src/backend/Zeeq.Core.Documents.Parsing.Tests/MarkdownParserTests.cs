namespace Zeeq.Core.Documents.Parsing.Tests;

public class MarkdownParserTests
{
    // ── Prototype parity (Sections/Snippets) ───────────────────

    [Test]
    public async Task Parse_PrototypeParity_SectionCount()
    {
        var markdown = await File.ReadAllTextAsync("sample1.md");
        var doc = MarkdownParser.Parse(markdown, fileName: "");

        await Assert.That(doc.Sections.Count).IsGreaterThan(0);
        await Assert.That(doc.Sections.Count).IsEqualTo(10);
    }

    [Test]
    public async Task Parse_PrototypeParity_SnippetCount()
    {
        var markdown = await File.ReadAllTextAsync("sample1.md");
        var doc = MarkdownParser.Parse(markdown, fileName: "");

        await Assert.That(doc.Snippets.Count).IsGreaterThan(0);
        await Assert.That(doc.Snippets.Count).IsEqualTo(8);
    }

    // ── Title resolution ───────────────────────────────────────

    [Test]
    public async Task Parse_TitleFromFrontMatter_FrontMatterWins()
    {
        var md = "---\ntitle: My Doc\n---\n# Other Title\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Title).IsEqualTo("My Doc");
    }

    [Test]
    public async Task Parse_TitleFromH1_FallsBackWhenNoFmTitle()
    {
        var md = "# Hello World\n\nContent here.";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Title).IsEqualTo("Hello World");
    }

    [Test]
    public async Task Parse_TitleFromFileName_FallsBackWhenNoH1()
    {
        var md = "Just text, no heading.";
        var doc = MarkdownParser.Parse(md, fileName: "my-doc");

        await Assert.That(doc.Title).IsEqualTo("my-doc");
    }

    [Test]
    public async Task Parse_TitleEmptyFileName_EmptyTitle()
    {
        var md = "Just text, no heading.";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Title).IsEqualTo("");
    }

    // ── Keywords ────────────────────────────────────────────────

    [Test]
    public async Task Parse_FrontMatterWithKeywordsCsv_ExtractsKeywords()
    {
        var md = "---\nkeywords: a, b, c\n---\n# Title\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Keywords).IsEquivalentTo(["a", "b", "c"]);
    }

    [Test]
    public async Task Parse_FrontMatterWithTagsBlockList_ExtractsKeywords()
    {
        var md = "---\ntags:\n  - csharp\n  - dotnet\n  - efcore\n---\n# Title\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Keywords).IsEquivalentTo(["csharp", "dotnet", "efcore"]);
    }

    [Test]
    public async Task Parse_FrontMatterWithKeywordsInlineArray_ExtractsKeywords()
    {
        var md = "---\nkeywords: [csharp, dotnet, efcore]\n---\n# Title\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Keywords).IsEquivalentTo(["csharp", "dotnet", "efcore"]);
    }

    [Test]
    public async Task Parse_KeywordsTrimmed_NoLeadingTrailingWhitespace()
    {
        var md = "---\nkeywords:  a , b  \n---\n# Title\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Keywords).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Parse_FrontMatterMissing_EmptyFrontMatter()
    {
        var md = "# Just a heading\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.FrontMatter).IsEqualTo("");
        await Assert.That(doc.Keywords.Count).IsEqualTo(0);
    }

    // ── Headings ────────────────────────────────────────────────

    [Test]
    public async Task Parse_Headings_FlatListInOrder()
    {
        var md = "# A\n\n## B\n\n### C\n\n## D\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Headings.SequenceEqual(["A", "B", "C", "D"])).IsTrue();
    }

    [Test]
    public async Task Parse_Headings_NoMarkers()
    {
        var md = "## Getting Started\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Headings[0]).IsEqualTo("Getting Started");
    }

    [Test]
    public async Task Parse_Headings_TrailingHashStripped()
    {
        var md = "## Section ##\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Headings[0]).IsEqualTo("Section");
    }

    // ── Sections ────────────────────────────────────────────────

    [Test]
    public async Task Parse_Sections_BodyUnderHeading()
    {
        var md = "# A\n\ntext under A\n\n## B\n\ntext under B\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Sections.Count).IsEqualTo(2);
        await Assert.That(doc.Sections[0].Body).Contains("text under A");
        await Assert.That(doc.Sections[1].Body).Contains("text under B");
    }

    [Test]
    public async Task Parse_Sections_HeadingPathHierarchical()
    {
        var md = "# H1\n\n## H2\n\ntext\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Sections[0].HeadingPath).Contains("H1");
        await Assert.That(doc.Sections[0].HeadingPath).Contains("H2");
    }

    // ── Snippets (code block tag resolution) ────────────────────

    [Test]
    public async Task Parse_Snippets_CsharpComment_TagFromFirstLine()
    {
        var md = "# H\n\n```cs\n// SomeTag\ncode here\n```\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Snippets.Count).IsEqualTo(1);
        await Assert.That(doc.Snippets[0].Tag).IsEqualTo("SomeTag");
    }

    [Test]
    public async Task Parse_Snippets_PythonComment_TagFromFirstLine()
    {
        var md = "# H\n\n```py\n# SomeTag\ncode here\n```\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Snippets.Count).IsEqualTo(1);
        await Assert.That(doc.Snippets[0].Tag).IsEqualTo("SomeTag");
    }

    [Test]
    public async Task Parse_Snippets_SqlComment_TagFromFirstLine()
    {
        var md = "# H\n\n```sql\n-- SomeTag\nSELECT 1\n```\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Snippets.Count).IsEqualTo(1);
        await Assert.That(doc.Snippets[0].Tag).IsEqualTo("SomeTag");
    }

    [Test]
    public async Task Parse_Snippets_HtmlComment_TagFromFirstLine()
    {
        var md = "# H\n\n```html\n<!-- SomeTag -->\n<div/>\n```\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Snippets.Count).IsEqualTo(1);
        await Assert.That(doc.Snippets[0].Tag).IsEqualTo("SomeTag");
    }

    [Test]
    public async Task Parse_Snippets_CssBlockComment_TagFromFirstLine()
    {
        var md = "# H\n\n```css\n/* SomeTag */\nbody {}\n```\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Snippets.Count).IsEqualTo(1);
        await Assert.That(doc.Snippets[0].Tag).IsEqualTo("SomeTag");
    }

    [Test]
    public async Task Parse_Snippets_NoTagFallback_EmptyTag()
    {
        var md = "# H\n\n```cs\ncode here\n```\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Snippets.Count).IsEqualTo(1);
        await Assert.That(doc.Snippets[0].Tag).IsEqualTo("");
    }

    [Test]
    public async Task Parse_Snippets_Language_ExtractedFromFence()
    {
        var md = "```cs\ncode\n```\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Snippets.Count).IsEqualTo(1);
        await Assert.That(doc.Snippets[0].Language).IsEqualTo("cs");
    }

    // ── Pre-heading content ─────────────────────────────────────

    [Test]
    public async Task Parse_TextBeforeFirstHeading_ProducesSection()
    {
        var md = "Intro text.\n\n# Heading\n\nText under heading.\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Sections.Count).IsEqualTo(2);
        await Assert.That(doc.Sections[0].Body).Contains("Intro text.");
        await Assert.That(doc.Sections[0].Header).IsEqualTo("");
        await Assert.That(doc.Sections[0].HeadingPath).IsEqualTo("");
    }

    [Test]
    public async Task Parse_OnlyTextNoHeading_ProducesSingleSection()
    {
        var md = "Just text, no heading.";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Sections.Count).IsEqualTo(1);
        await Assert.That(doc.Sections[0].Body).Contains("Just text");
        await Assert.That(doc.Sections[0].Header).IsEqualTo("");
    }

    // ── Edge cases ──────────────────────────────────────────────

    [Test]
    public async Task Parse_EmptyString_ReturnsEmpty()
    {
        var doc = MarkdownParser.Parse("", fileName: "");

        await Assert.That(doc.Sections.Count).IsEqualTo(0);
        await Assert.That(doc.Snippets.Count).IsEqualTo(0);
        await Assert.That(doc.Title).IsEqualTo("");
    }

    [Test]
    public async Task Parse_OnlyFrontMatter_ContentEmpty()
    {
        var md = "---\ntitle: x\n---\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Sections.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_FrontMatterOpenButNotClosed_TreatedAsContent()
    {
        var md = "---\ntitle: x\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.FrontMatter).IsEqualTo("");
    }

    [Test]
    public async Task Parse_FrontMatterUnclosed_BodyParsedFromFirstHeading()
    {
        // Unclosed --- fence: heading and content after it should still be parsed.
        var md = "---\ntitle: Orphan\n# Real Heading\n\nbody text\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.FrontMatter).IsEqualTo("");
        await Assert.That(doc.Headings).Contains("Real Heading");
        await Assert.That(doc.Sections.Count).IsGreaterThan(0);
        await Assert.That(doc.Sections[0].Body).Contains("body text");
    }

    [Test]
    public async Task Parse_BlockListTagsFollowedByTitle_TitleIsResolved()
    {
        // Block-list tags used to consume the closing --- fence, dropping all subsequent fields
        // and leaving contentStart = len so the body was never parsed.
        var md = "---\ntags:\n  - csharp\n  - dotnet\ntitle: My Article\n---\n# Body Heading\n\nbody text\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Title).IsEqualTo("My Article");
        await Assert.That(doc.Keywords).IsEquivalentTo(["csharp", "dotnet"]);
        await Assert.That(doc.Headings).Contains("Body Heading");
        await Assert.That(doc.Sections.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Parse_BlockListTagsLastField_BodyIsParsed()
    {
        // Block-list tags as the last front-matter field before --- must not consume the fence.
        var md = "---\ntags:\n  - csharp\n  - dotnet\n  - efcore\n---\n# Title\n\nbody text\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Keywords).IsEquivalentTo(["csharp", "dotnet", "efcore"]);
        await Assert.That(doc.Headings).Contains("Title");
        await Assert.That(doc.Sections.Count).IsGreaterThan(0);
        await Assert.That(doc.Sections[0].Body).Contains("body text");
    }

    [Test]
    public async Task Parse_SixHashHeadingMaxLevel()
    {
        var md = "###### H6\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Headings[0]).IsEqualTo("H6");
    }

    [Test]
    public async Task Parse_SevenHashNotHeading()
    {
        var md = "####### not a heading\n";
        var doc = MarkdownParser.Parse(md, fileName: "");

        await Assert.That(doc.Headings.Count).IsEqualTo(0);
    }
}
