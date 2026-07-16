namespace Zeeq.Core.Documents.Tests;

/// <summary>
/// Unit tests for document title, path, and keyword normalization rules.
/// </summary>
public sealed class DocumentNormalizerTests
{
    [Test]
    public async Task Normalize_UpperCase_LowersCase()
    {
        var normalized = DocumentNormalizer.Normalize("Hello");

        await Assert.That(normalized).IsEqualTo("hello");
    }

    [Test]
    public async Task Normalize_SpecialChars_StripsDisallowed()
    {
        var normalized = DocumentNormalizer.Normalize("foo(bar)");

        await Assert.That(normalized).IsEqualTo("foobar");
    }

    [Test]
    public async Task Normalize_AllowedChars_PreservesValue()
    {
        var normalized = DocumentNormalizer.Normalize("a/b-c+d.e");

        await Assert.That(normalized).IsEqualTo("a/b-c+d.e");
    }

    [Test]
    public async Task NormalizePath_MissingLeadingSlash_AddsSlash()
    {
        var normalized = DocumentNormalizer.NormalizePath("foo/bar.md");

        await Assert.That(normalized).IsEqualTo("/foo/bar.md");
    }

    [Test]
    public async Task NormalizePath_MissingExtension_AddsExtension()
    {
        var normalized = DocumentNormalizer.NormalizePath("foo/bar");

        await Assert.That(normalized).IsEqualTo("/foo/bar.md");
    }

    [Test]
    public async Task NormalizePath_DuplicateSlashes_CollapsesSlashes()
    {
        var normalized = DocumentNormalizer.NormalizePath("foo//bar.md");

        await Assert.That(normalized).IsEqualTo("/foo/bar.md");
    }

    [Test]
    public async Task NormalizePath_EmptyPath_ThrowsArgumentException()
    {
        string Act() => DocumentNormalizer.NormalizePath("");

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task NormalizePath_WhitespacePath_ThrowsArgumentException()
    {
        string Act() => DocumentNormalizer.NormalizePath("   ");

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task NormalizePath_CurrentDirectorySegment_ThrowsArgumentException()
    {
        string Act() => DocumentNormalizer.NormalizePath("docs/./guide.md");

        await Assert.That(Act).Throws<ArgumentException>();
    }

    [Test]
    public async Task NormalizePath_ParentDirectorySegment_ThrowsArgumentException()
    {
        string Act() => DocumentNormalizer.NormalizePath("docs/../guide.md");

        await Assert.That(Act).Throws<ArgumentException>();
    }

    // ── LibraryDocument.RenameTo ────────────────────────────────

    [Test]
    public async Task RenameTo_SamePath_DoesNotModifyPreviousPathsOrUpdatedAt()
    {
        var doc = new LibraryDocument
        {
            Path = "/docs/guide.md",
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        doc.RenameTo("/docs/guide.md");

        await Assert.That(doc.PreviousPaths).IsEmpty();
        await Assert.That(doc.UpdatedAt).IsEqualTo(DateTimeOffset.UnixEpoch);
    }

    [Test]
    public async Task NormalizeKeywords_DuplicateCase_Deduplicates()
    {
        var normalized = DocumentNormalizer.NormalizeKeywords(["a", "A"]);

        await Assert.That(normalized).IsEquivalentTo(["a"]);
    }

    [Test]
    public async Task NormalizeKeywords_Whitespace_TrimsValues()
    {
        var normalized = DocumentNormalizer.NormalizeKeywords([" a "]);

        await Assert.That(normalized).IsEquivalentTo(["a"]);
    }
}
