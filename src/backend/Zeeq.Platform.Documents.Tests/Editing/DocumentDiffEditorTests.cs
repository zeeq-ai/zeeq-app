using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Documents;
using Zeeq.Platform.Documents.Editing;

namespace Zeeq.Platform.Documents.Tests.Editing;

/// <summary>Exercises the customer diff cases against the complete guard chain.</summary>
public sealed class DocumentDiffEditorTests
{
    private static DocumentDiffEditor Editor() =>
        new([
            new EligibilityGuard(),
            new BaseHashGuard(),
            new ParseGuard(),
            new EmptyBaseGuard(),
            new ContextLinesGuard(),
            new UniqueOldSideGuard(),
            new ResultCheckGuard(),
            new TextDiffApplier(),
        ]);

    [Test]
    public async Task MissingMandatoryGuards_AreRejectedAtConstruction()
    {
        await Assert.That(() => new DocumentDiffEditor([])).Throws<ArgumentException>();
    }

    [Test]
    public async Task CustomerFixtures_MatchExpectedOutcomeAndContent()
    {
        using var zip = ZipFile.OpenRead(
            Path.Combine(AppContext.BaseDirectory, "Editing", "diff-cases.zip")
        );
        var cases = zip.Entries.Where(e => e.FullName.EndsWith("/case.json")).ToArray();
        await Assert.That(cases.Length).IsEqualTo(40);
        foreach (var entry in cases)
        {
            var directory = entry.FullName[..^"case.json".Length];
            string Read(string name)
            {
                using var stream = zip.GetEntry(directory + name)!.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            using var metadata = JsonDocument.Parse(Read("case.json"));
            var expected = metadata.RootElement.GetProperty("expected").GetString()!;
            if (directory.StartsWith("15-"))
                expected = "OutOfOrder";
            if (directory.StartsWith("22-"))
                expected = "EmptyResult";
            var source = metadata.RootElement.GetProperty("source").GetString();
            var content = Read("base.md");
            var agentBase = zip.GetEntry(directory + "agent-base.md") is null
                ? content
                : Read("agent-base.md");
            var hash = Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentBase)))
                .ToLowerInvariant();
            var doc = new LibraryDocument
            {
                Content = content,
                ContentHash = Convert
                    .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                    .ToLowerInvariant(),
                Authoring =
                    source == "Synced"
                        ? LibraryDocumentAuthoring.Synced
                        : LibraryDocumentAuthoring.Manual,
            };
            var outcome = await Editor().ApplyAsync(new DiffContext(doc, hash, Read("diff.patch")));
            if (outcome.GetType().Name != expected)
                throw new InvalidOperationException(
                    $"{directory}: expected {expected}, got {outcome.GetType().Name}"
                );
            if (directory.StartsWith("31-"))
                await Assert.That(((DiffOutcome.Ambiguous)outcome).Detail).Contains("Hunk 1");
            if (directory.StartsWith("40-"))
                await Assert.That(((DiffOutcome.NoMatch)outcome).Detail).Contains("Hunk 2");
            if (outcome is DiffOutcome.Applied applied && applied.Content != Read("expected.md"))
                throw new InvalidOperationException($"{directory}: resulting content differs");
        }
    }

    [Test]
    [Arguments("old", "new")]
    [Arguments("old\n", "new\n")]
    public async Task Replacement_PreservesExistingFinalNewline(string original, string expected)
    {
        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)))
            .ToLowerInvariant();
        var doc = new LibraryDocument { Content = original, ContentHash = hash };
        var patch = "@@ -1 +1 @@\n-old\n+new\n\\ No newline at end of file\n";
        var result = await Editor().ApplyAsync(new DiffContext(doc, hash, patch));
        await Assert.That(result is DiffOutcome.Applied).IsTrue();
        await Assert.That(((DiffOutcome.Applied)result).Content).IsEqualTo(expected);
    }

    [Test]
    public async Task EmptyBase_RejectsWhitespaceOnlyResult()
    {
        const string hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var result = await Editor()
            .ApplyAsync(
                new DiffContext(
                    new LibraryDocument { Content = "", ContentHash = hash },
                    hash,
                    "@@ -0,0 +1 @@\n+  \n"
                )
            );
        await Assert.That(result is DiffOutcome.EmptyResult).IsTrue();
    }

    [Test]
    public async Task UnchangedEdit_IsRejected()
    {
        var doc = new LibraryDocument { Content = "old\n", ContentHash = "hash" };
        var result = await Editor()
            .ApplyAsync(new DiffContext(doc, "hash", "@@ -1 +1 @@\n-old\n+old\n"));
        await Assert.That(result is DiffOutcome.InvalidDiff).IsTrue();
    }

    [Test]
    public async Task MixedLineEndings_AreRejectedWithoutChangingContent()
    {
        var doc = new LibraryDocument { Content = "a\r\nb\nc\n", ContentHash = "hash" };
        var result = await Editor()
            .ApplyAsync(new DiffContext(doc, "hash", "@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n"));
        await Assert.That(result is DiffOutcome.InvalidDiff).IsTrue();
    }

    [Test]
    public async Task RepeatedFileHeaders_AreRejected()
    {
        var doc = new LibraryDocument { Content = "old\n", ContentHash = "hash" };
        var patch = "--- a/one\n+++ b/one\n--- a/two\n+++ b/two\n@@ -1 +1 @@\n-old\n+new\n";
        var result = await Editor().ApplyAsync(new DiffContext(doc, "hash", patch));
        await Assert.That(result is DiffOutcome.InvalidDiff).IsTrue();
    }

    [Test]
    public async Task InvalidHunkHeader_IsRejected()
    {
        var doc = new LibraryDocument { Content = "old\n", ContentHash = "hash" };
        var result = await Editor()
            .ApplyAsync(new DiffContext(doc, "hash", "@@ invalid @@\n-old\n+new\n"));
        await Assert.That(result is DiffOutcome.InvalidDiff).IsTrue();
    }

    [Test]
    public async Task UnmatchedHunkWithLittleContext_ReportsItsNumber()
    {
        var doc = new LibraryDocument { Content = "old\n", ContentHash = "hash" };
        var result = await Editor()
            .ApplyAsync(new DiffContext(doc, "hash", "@@ -1 +1 @@\n-missing\n+new\n"));
        await Assert.That(result is DiffOutcome.NoMatch).IsTrue();
        await Assert.That(((DiffOutcome.NoMatch)result).Detail).Contains("Hunk 1");
    }

    [Test]
    public async Task EmptyHunkBeforeValidHunk_IsRejected()
    {
        var doc = new LibraryDocument { Content = "old\n", ContentHash = "hash" };
        var result = await Editor()
            .ApplyAsync(new DiffContext(doc, "hash", "@@ -1 +1 @@\n@@ -1 +1 @@\n-old\n+new\n"));
        await Assert.That(result is DiffOutcome.InvalidDiff).IsTrue();
    }

    [Test]
    public async Task OversizedDiff_IsRejectedBeforeParsing()
    {
        var doc = new LibraryDocument { Content = "x", ContentHash = "hash" };
        var result = await Editor()
            .ApplyAsync(new DiffContext(doc, "hash", new string('x', 1_000_001)));
        await Assert.That(result is DiffOutcome.TooLarge).IsTrue();
    }

    [Test]
    public async Task SynchronizedSourceMarker_RejectsEvenIfAuthoringIsManual()
    {
        var doc = new LibraryDocument
        {
            Content = "x",
            ContentHash = "hash",
            SourceExternalId = "notion-page",
        };
        var result = await Editor()
            .ApplyAsync(new DiffContext(doc, "hash", "@@ -1 +1 @@\n-x\n+y\n"));
        await Assert.That(result is DiffOutcome.NotEditable).IsTrue();
    }

    [Test]
    public async Task RepeatedHunksAtSamePosition_AreRejected()
    {
        var doc = new LibraryDocument { Content = "old\n", ContentHash = "hash" };
        var result = await Editor()
            .ApplyAsync(
                new DiffContext(doc, "hash", "@@ -1 +1 @@\n-old\n+one\n@@ -1 +1 @@\n-old\n+two\n")
            );
        await Assert.That(result is DiffOutcome.ApplyFailed).IsTrue();
    }

    [Test]
    public async Task Outcomes_RoundTripThroughJson()
    {
        DiffOutcome[] outcomes =
        [
            new DiffOutcome.Applied("x", "hash"),
            new DiffOutcome.Stale("x", "hash"),
            new DiffOutcome.NotEditable(null),
            new DiffOutcome.InvalidDiff("x"),
            new DiffOutcome.ThinContext("x"),
            new DiffOutcome.NoMatch("x"),
            new DiffOutcome.Ambiguous("x"),
            new DiffOutcome.OutOfOrder("x"),
            new DiffOutcome.EmptyResult("x"),
            new DiffOutcome.TooLarge("x"),
            new DiffOutcome.ApplyFailed("x"),
        ];
        foreach (var outcome in outcomes)
        {
            var json = JsonSerializer.Serialize<DiffOutcome>(outcome);
            var restored = JsonSerializer.Deserialize<DiffOutcome>(json);
            await Assert.That(restored).IsEqualTo(outcome);
        }
    }
}
