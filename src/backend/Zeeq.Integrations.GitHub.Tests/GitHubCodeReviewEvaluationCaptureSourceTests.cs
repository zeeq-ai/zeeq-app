using Octokit;
using Zeeq.Platform.CodeReviews;

namespace Zeeq.Integrations.GitHub.Tests;

/// <summary>
/// Bounded file-fetch tests for the evaluation capture source.
/// </summary>
/// <remarks>
/// A large pull request must not materialize every patch before the snapshot limit is checked.
/// These tests use a fake page fetcher, so they are deterministic and prove that later pages are
/// never requested once the accumulated budget is exceeded.
/// </remarks>
public sealed class GitHubCodeReviewEvaluationCaptureSourceTests
{
    [Test]
    public async Task FetchFilesWithinBudget_StopsBeforeLaterPagesWhenBudgetExceeded()
    {
        var pagesRequested = 0;

        var exception = await Assert.ThrowsAsync<CodeReviewEvaluationCaptureException>(() =>
            GitHubCodeReviewEvaluationPullRequestCaptureSource.FetchFilesWithinBudgetAsync(
                (page, _) =>
                {
                    pagesRequested++;
                    return Task.FromResult(Page(page, count: 100, patchLength: 30_000));
                },
                file => file.Patch.Length,
                CancellationToken.None
            )
        );

        await Assert.That(exception!.Code).IsEqualTo("snapshot_too_large");

        // 100 x 30 KB is ~3 MB per page, so the limit is crossed inside page 2 and page 3 is
        // never asked for.
        await Assert.That(pagesRequested).IsEqualTo(2);
    }

    [Test]
    public async Task FetchFilesWithinBudget_ReturnsAllFilesWhenUnderBudget()
    {
        var pagesRequested = 0;

        var files =
            await GitHubCodeReviewEvaluationPullRequestCaptureSource.FetchFilesWithinBudgetAsync(
                (page, _) =>
                {
                    pagesRequested++;

                    // A short page is the last page.
                    return Task.FromResult(
                        page == 1
                            ? Page(page, count: 2, patchLength: 128)
                            : Page(page, count: 0, patchLength: 0)
                    );
                },
                file => file.Patch.Length,
                CancellationToken.None
            );

        await Assert.That(pagesRequested).IsEqualTo(1);
        await Assert.That(files.Count).IsEqualTo(2);
        await Assert.That(files[0].Patch).IsNotNull();
    }

    private static IReadOnlyList<FakeFile> Page(int page, int count, int patchLength) =>
        [
            .. Enumerable
                .Range(0, count)
                .Select(index => new FakeFile(
                    $"src/page{page}/File{index}.cs",
                    null,
                    new string('x', patchLength)
                )),
        ];

    private sealed record FakeFile(string Path, string? PreviousPath, string Patch);
}
