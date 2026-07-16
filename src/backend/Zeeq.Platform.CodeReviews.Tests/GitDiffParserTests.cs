namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class GitDiffParserTests
{
    [Test]
    public async Task Parse_WithEmptyDiff_ReturnsEmptyList()
    {
        var files = new GitDiffParser().Parse(string.Empty);

        await Assert.That(files).IsEmpty();
    }

    [Test]
    public async Task Parse_WithModifiedUnquotedPath_ReturnsPatchAndPath()
    {
        var files = Parse(
            """
            diff --git a/src/backend/Code.cs b/src/backend/Code.cs
            index 1111111..2222222 100644
            --- a/src/backend/Code.cs
            +++ b/src/backend/Code.cs
            @@ -1 +1 @@
            -var value = 0;
            +var value = 1;
            """
        );

        await Assert.That(files).Count().IsEqualTo(1);
        await Assert.That(files[0].Path).IsEqualTo("src/backend/Code.cs");
        await Assert.That(files[0].PreviousPath).IsEqualTo("src/backend/Code.cs");
        await Assert.That(files[0].MutationState).IsEqualTo(UploadedDiffFileMutationState.Modified);
        await Assert.That(files[0].Patch).Contains("+var value = 1;");
    }

    [Test]
    public async Task Parse_WithQuotedPath_UnescapesGitQuotedCharacters()
    {
        var files = Parse(
            """
            diff --git "a/docs/quote\" and tab\t.md" "b/docs/quote\" and tab\t.md"
            index 1111111..2222222 100644
            --- "a/docs/quote\" and tab\t.md"
            +++ "b/docs/quote\" and tab\t.md"
            @@ -1 +1 @@
            -old
            +new
            """
        );

        await Assert.That(files[0].Path).IsEqualTo("docs/quote\" and tab\t.md");
        await Assert.That(files[0].PreviousPath).IsEqualTo("docs/quote\" and tab\t.md");
    }

    [Test]
    public async Task Parse_WithAddedAndDeletedFiles_UsesDevNullMarkersForState()
    {
        var files = Parse(
            """
            diff --git a/new.cs b/new.cs
            new file mode 100644
            index 0000000..1111111
            --- /dev/null
            +++ b/new.cs
            @@ -0,0 +1 @@
            +new
            diff --git a/old.cs b/old.cs
            deleted file mode 100644
            index 1111111..0000000
            --- a/old.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -old
            """
        );

        await Assert.That(files).Count().IsEqualTo(2);
        await Assert.That(files[0].Path).IsEqualTo("new.cs");
        await Assert.That(files[0].PreviousPath).IsNull();
        await Assert.That(files[0].MutationState).IsEqualTo(UploadedDiffFileMutationState.Added);
        await Assert.That(files[1].Path).IsEqualTo("old.cs");
        await Assert.That(files[1].PreviousPath).IsEqualTo("old.cs");
        await Assert.That(files[1].MutationState).IsEqualTo(UploadedDiffFileMutationState.Deleted);
    }

    [Test]
    public async Task Parse_WithRenamedAndCopiedFiles_UsesMetadataPaths()
    {
        var files = Parse(
            """
            diff --git a/old-name.cs b/new-name.cs
            similarity index 98%
            rename from old-name.cs
            rename to new-name.cs
            index 1111111..2222222 100644
            --- a/old-name.cs
            +++ b/new-name.cs
            @@ -1 +1 @@
            -old
            +new
            diff --git a/source.cs b/copy.cs
            similarity index 100%
            copy from source.cs
            copy to copy.cs
            """
        );

        await Assert.That(files).Count().IsEqualTo(2);
        await Assert.That(files[0].Path).IsEqualTo("new-name.cs");
        await Assert.That(files[0].PreviousPath).IsEqualTo("old-name.cs");
        await Assert.That(files[0].MutationState).IsEqualTo(UploadedDiffFileMutationState.Renamed);
        await Assert.That(files[1].Path).IsEqualTo("copy.cs");
        await Assert.That(files[1].PreviousPath).IsEqualTo("source.cs");
        await Assert.That(files[1].MutationState).IsEqualTo(UploadedDiffFileMutationState.Copied);
    }

    [Test]
    public async Task Parse_WithBinaryFile_ReturnsBinaryMutationState()
    {
        var files = Parse(
            """
            diff --git a/assets/icon.png b/assets/icon.png
            index 1111111..2222222 100644
            Binary files a/assets/icon.png and b/assets/icon.png differ
            """
        );

        await Assert.That(files[0].Path).IsEqualTo("assets/icon.png");
        await Assert.That(files[0].MutationState).IsEqualTo(UploadedDiffFileMutationState.Binary);
        await Assert.That(files[0].Patch).Contains("Binary files");
    }

    [Test]
    public async Task ToCodeReviewFileSnapshot_MapsMutationStateAndPatch()
    {
        var uploaded = new UploadedDiffFile(
            "new.cs",
            "old.cs",
            UploadedDiffFileMutationState.Renamed,
            "rename patch"
        );

        var snapshot = uploaded.ToCodeReviewFileSnapshot();

        await Assert.That(snapshot.Path).IsEqualTo("new.cs");
        await Assert.That(snapshot.PreviousPath).IsEqualTo("old.cs");
        await Assert.That(snapshot.MutationState).IsEqualTo(CodeReviewFileMutationState.Renamed);
        await Assert.That(snapshot.Patch).IsEqualTo("rename patch");
    }

    private static IReadOnlyList<UploadedDiffFile> Parse(string diff) =>
        new GitDiffParser().Parse(diff);
}
