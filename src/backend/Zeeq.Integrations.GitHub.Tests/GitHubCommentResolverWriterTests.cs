using Zeeq.Core.Models;
using Zeeq.Platform.CodeReviews;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeeq.Integrations.GitHub.Tests;

/// <summary>
/// Tests for GitHub comment resolver and writer behavior.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Integrations.GitHub.Tests --output detailed --disable-logo --treenode-filter "/*/*/GitHubCommentResolverWriterTests/*"
/// </summary>
public sealed class GitHubCommentResolverWriterTests
{
    private static readonly GitHubCommentTargetSelector Target = new(
        OrganizationId: "org_1",
        RepositoryId: "repo_1",
        PullRequestNumber: 42,
        Kind: GitHubCommentTargetKind.PullRequestSummary,
        ScopeKey: "root"
    );

    [Test]
    public async Task ResolveAsync_ReturnsDomWhenStoredIssueCommentIdIsValid()
    {
        var client = new FakeGitHubCommentClient();
        client.IssueCommentsById[1001] = new GitHubCommentCandidate(1001, Body("Stored header"));
        var resolver = new GitHubCommentResolver();

        var resolution = await resolver.ResolveAsync(
            client,
            Target,
            "owner/repo",
            storedCommentId: 1001,
            CancellationToken.None
        );

        await Assert.That(resolution).IsNotNull();
        await Assert.That(resolution!.CommentId).IsEqualTo(1001);
        await Assert
            .That(resolution.Dom.FindSection(GitHubCommentMarkers.PullRequestHeader)?.Content)
            .Contains("Stored header");
        await Assert.That(client.IssueCommentScanYieldCount).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveAsync_FallsBackToScanWhenStoredIssueCommentIsMissing()
    {
        var client = new FakeGitHubCommentClient();
        client.IssueComments.Add(new GitHubCommentCandidate(2001, "ordinary comment"));
        client.IssueComments.Add(new GitHubCommentCandidate(2002, Body("Recovered header")));
        var resolver = new GitHubCommentResolver();

        var resolution = await resolver.ResolveAsync(
            client,
            Target,
            "owner/repo",
            storedCommentId: 1001,
            CancellationToken.None
        );

        await Assert.That(resolution).IsNotNull();
        await Assert.That(resolution!.CommentId).IsEqualTo(2002);
        await Assert.That(client.IssueCommentScanYieldCount).IsEqualTo(2);
    }

    [Test]
    public async Task ResolveAsync_ReturnsNullWhenScanFindsNoTargetMarker()
    {
        var client = new FakeGitHubCommentClient();
        client.IssueComments.Add(new GitHubCommentCandidate(2001, "ordinary comment"));
        client.IssueComments.Add(new GitHubCommentCandidate(2002, ReviewThreadBody()));
        var resolver = new GitHubCommentResolver();

        var resolution = await resolver.ResolveAsync(
            client,
            Target,
            "owner/repo",
            storedCommentId: null,
            CancellationToken.None
        );

        await Assert.That(resolution).IsNull();
        await Assert.That(client.IssueCommentScanYieldCount).IsEqualTo(2);
    }

    [Test]
    public async Task ResolveAsync_StopsScanningWhenTargetMarkerIsFound()
    {
        var client = new FakeGitHubCommentClient();
        client.IssueComments.Add(new GitHubCommentCandidate(2001, "ordinary comment"));
        client.IssueComments.Add(new GitHubCommentCandidate(2002, Body("Recovered header")));
        client.IssueComments.Add(new GitHubCommentCandidate(2003, Body("Should not be read")));
        var resolver = new GitHubCommentResolver();

        var resolution = await resolver.ResolveAsync(
            client,
            Target,
            "owner/repo",
            storedCommentId: null,
            CancellationToken.None
        );

        await Assert.That(resolution).IsNotNull();
        await Assert.That(client.IssueCommentScanYieldCount).IsEqualTo(2);
    }

    [Test]
    public async Task UpsertAsync_UpdatesExistingIssueCommentId()
    {
        var client = new FakeGitHubCommentClient();
        var writer = CreateWriter();

        var commentId = await writer.UpsertAsync(
            client,
            Target,
            "owner/repo",
            existingCommentId: 1001,
            body: "new body",
            CancellationToken.None
        );

        await Assert.That(commentId).IsEqualTo(1001);
        await Assert.That(client.UpdatedIssueComments).IsEquivalentTo([(1001L, "new body")]);
        await Assert.That(client.CreatedIssueComments).IsEmpty();
        await Assert.That(client.IssueCommentScanYieldCount).IsEqualTo(0);
    }

    [Test]
    public async Task UpsertAsync_ScansBeforeCreateWhenExistingIdIsStale()
    {
        var client = new FakeGitHubCommentClient();
        client.MissingIssueUpdateIds.Add(1001);
        client.IssueComments.Add(new GitHubCommentCandidate(2002, Body("Recovered header")));
        var writer = CreateWriter();

        var commentId = await writer.UpsertAsync(
            client,
            Target,
            "owner/repo",
            existingCommentId: 1001,
            body: "new body",
            CancellationToken.None
        );

        await Assert.That(commentId).IsEqualTo(2002);
        await Assert.That(client.UpdatedIssueComments).IsEquivalentTo([(2002L, "new body")]);
        await Assert.That(client.CreatedIssueComments).IsEmpty();
    }

    [Test]
    public async Task UpsertAsync_CreatesIssueCommentWhenNoExistingTargetIsFound()
    {
        var client = new FakeGitHubCommentClient { NextCreatedIssueCommentId = 3001 };
        client.IssueComments.Add(new GitHubCommentCandidate(2001, "ordinary comment"));
        var writer = CreateWriter();

        var commentId = await writer.UpsertAsync(
            client,
            Target,
            "owner/repo",
            existingCommentId: null,
            body: "new body",
            CancellationToken.None
        );

        await Assert.That(commentId).IsEqualTo(3001);
        await Assert.That(client.CreatedIssueComments).IsEquivalentTo([(42, "new body")]);
        await Assert.That(client.UpdatedIssueComments).IsEmpty();
    }

    private static string Body(string header) =>
        $"""
            <!-- (000000):{GitHubCommentMarkers.PullRequestRoot}:start -->
            <!-- (100000):{GitHubCommentMarkers.PullRequestHeader}:start -->
            {header}
            <!-- {GitHubCommentMarkers.PullRequestHeader}:end -->
            <!-- {GitHubCommentMarkers.PullRequestRoot}:end -->
            """;

    private static string ReviewThreadBody() =>
        """
            <!-- (000000):zeeq:review-thread-comment-root:start -->
            <!-- zeeq:review-thread-comment-root:end -->
            """;

    private static GitHubCommentWriter CreateWriter() =>
        new(NullLogger<GitHubCommentWriter>.Instance);

    private sealed class FakeGitHubCommentClient : IGitHubCommentClient
    {
        public Dictionary<long, GitHubCommentCandidate> IssueCommentsById { get; } = [];

        public List<GitHubCommentCandidate> IssueComments { get; } = [];

        public HashSet<long> MissingIssueUpdateIds { get; } = [];

        public List<(long CommentId, string Body)> UpdatedIssueComments { get; } = [];

        public List<(int PullRequestNumber, string Body)> CreatedIssueComments { get; } = [];

        public int IssueCommentScanYieldCount { get; private set; }

        public long NextCreatedIssueCommentId { get; init; } = 9001;

        public Task<GitHubCommentCandidate?> GetIssueCommentAsync(
            string ownerQualifiedRepoName,
            long commentId,
            CancellationToken cancellationToken
        ) => Task.FromResult(IssueCommentsById.GetValueOrDefault(commentId));

        public Task<GitHubCommentCandidate?> GetPullRequestReviewCommentAsync(
            string ownerQualifiedRepoName,
            long commentId,
            CancellationToken cancellationToken
        ) => Task.FromResult<GitHubCommentCandidate?>(null);

        public async IAsyncEnumerable<GitHubCommentCandidate> EnumerateIssueCommentsAsync(
            string ownerQualifiedRepoName,
            int pullRequestNumber,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken
        )
        {
            foreach (var comment in IssueComments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IssueCommentScanYieldCount++;
                yield return comment;
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<GitHubCommentCandidate> EnumeratePullRequestReviewCommentsAsync(
            string ownerQualifiedRepoName,
            int pullRequestNumber,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<long> CreateIssueCommentAsync(
            string ownerQualifiedRepoName,
            int pullRequestNumber,
            string body,
            CancellationToken cancellationToken
        )
        {
            CreatedIssueComments.Add((pullRequestNumber, body));
            return Task.FromResult(NextCreatedIssueCommentId);
        }

        public Task<long> UpdateIssueCommentAsync(
            string ownerQualifiedRepoName,
            long commentId,
            string body,
            CancellationToken cancellationToken
        )
        {
            if (MissingIssueUpdateIds.Contains(commentId))
            {
                throw new GitHubCommentNotFoundException(
                    commentId,
                    new InvalidOperationException()
                );
            }

            UpdatedIssueComments.Add((commentId, body));
            return Task.FromResult(commentId);
        }

        public Task<long> UpdatePullRequestReviewCommentAsync(
            string ownerQualifiedRepoName,
            long commentId,
            string body,
            CancellationToken cancellationToken
        ) => Task.FromResult(commentId);

        public Task<long> CreatePullRequestReviewReplyAsync(
            string ownerQualifiedRepoName,
            int pullRequestNumber,
            long parentCommentId,
            string body,
            CancellationToken cancellationToken
        ) => Task.FromResult(parentCommentId + 1);
    }
}
