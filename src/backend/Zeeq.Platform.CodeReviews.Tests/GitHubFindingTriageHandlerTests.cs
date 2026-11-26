using NSubstitute;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class GitHubFindingTriageHandlerTests
{
    [Test]
    public async Task ExistingReviewReply_IsNotDuplicatedOnRetry()
    {
        var clients = Substitute.For<IGitHubCommentClientFactory>();
        var client = Substitute.For<IGitHubCommentClient>();
        clients.CreateForOrganizationAsync("org", Arg.Any<CancellationToken>()).Returns(client);
        client
            .EnumeratePullRequestReviewCommentsAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(ExistingReviewReply());
        var handler = new GitHubFindingTriageHandler(
            Substitute.For<IActiveGitHubAliasStore>(),
            Substitute.For<ICodeReviewFindingTriageService>(),
            Substitute.For<IZeeqMessagePublisher>(),
            clients,
            new CodeReviewSettings(),
            CreateLeases()
        );
        await handler.HandleAsync(
            "org",
            null,
            "repo",
            "owner/repo",
            2,
            12,
            "/zeeq clear abcde.F1",
            "Alice",
            "MEMBER",
            DateTimeOffset.UtcNow,
            null,
            "created",
            "delivery",
            new ZeeqTraceContext(null, null),
            GitHubCommentReactionTargetKind.PullRequestReviewComment,
            CancellationToken.None
        );
        await client
            .DidNotReceive()
            .CreatePullRequestReviewReplyAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<long>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    private static async IAsyncEnumerable<GitHubCommentCandidate> ExistingReviewReply()
    {
        await Task.CompletedTask;
        yield return new GitHubCommentCandidate(
            42,
            "<!-- zeeq:triage-error:PullRequestReviewComment:12 -->\nError"
        );
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MissingTimestampOrStaleEvent_ProducesConfusedAndNoRender(
        bool missingTimestamp
    )
    {
        var aliases = Substitute.For<IActiveGitHubAliasStore>();
        aliases
            .FindActiveGitHubAliasAsync("org", "alice", Arg.Any<CancellationToken>())
            .Returns(
                new UserAlias
                {
                    Id = "alias",
                    OrganizationId = "org",
                    UserId = "user",
                    Kind = UserAliasKind.GitHub,
                    DisplayValue = "Alice",
                    NormalizedValue = "alice",
                }
            );
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var row = new CodeReviewFindingTriage
        {
            Id = "triage",
            OrganizationId = "org",
            OwnerQualifiedRepoName = "owner/repo",
            SourceReviewId = "cr_abcde",
            SourceReviewCreatedAtUtc = DateTimeOffset.UtcNow,
            OccurrenceKey = "F1",
            SemanticId = "some-valid-finding",
            Facet = "Security",
            FindingSummary = "Finding",
        };
        service
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new FindingTriageOutcome.IgnoredStale(row));
        var publisher = Substitute.For<IZeeqMessagePublisher>();
        var clients = Substitute.For<IGitHubCommentClientFactory>();
        var client = Substitute.For<IGitHubCommentClient>();
        clients.CreateForOrganizationAsync("org", Arg.Any<CancellationToken>()).Returns(client);
        client
            .EnumerateIssueCommentsAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(EmptyComments());
        var handler = new GitHubFindingTriageHandler(
            aliases,
            service,
            publisher,
            clients,
            new CodeReviewSettings(),
            CreateLeases()
        );
        await handler.HandleAsync(
            "org",
            null,
            "repo",
            "owner/repo",
            2,
            12,
            "/zeeq fp abcde.F1 reason",
            "Alice",
            "MEMBER",
            missingTimestamp ? null : DateTimeOffset.UtcNow,
            null,
            "created",
            "delivery",
            new ZeeqTraceContext(null, null),
            GitHubCommentReactionTargetKind.IssueComment,
            CancellationToken.None
        );
        if (missingTimestamp)
            await service
                .DidNotReceive()
                .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
        else
            await service
                .Received(1)
                .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
        await publisher
            .Received(1)
            .PublishAsync(
                Arg.Is<GitHubCommentReactionRequested>(x =>
                    x.ReactionContent == GitHubCommentReactionContent.Confused
                ),
                Arg.Any<CancellationToken>()
            );
        await publisher
            .DidNotReceive()
            .PublishAsync(Arg.Any<GitHubCommentWriteRequested>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ConcurrentErrorReplies_OnlyLeaseOwnerPosts(bool renewalSucceeds)
    {
        var aliases = Substitute.For<IActiveGitHubAliasStore>();
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var publisher = Substitute.For<IZeeqMessagePublisher>();
        var clients = Substitute.For<IGitHubCommentClientFactory>();
        var client = Substitute.For<IGitHubCommentClient>();
        var leases = Substitute.For<IGitHubCommentLeaseStore>();
        leases
            .RenewAsync(
                Arg.Any<GitHubCommentLeaseKey>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(renewalSucceeds);
        leases
            .TryAcquireAsync(
                Arg.Any<GitHubCommentLeaseKey>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clients.CreateForOrganizationAsync("org", Arg.Any<CancellationToken>()).Returns(client);
        client
            .EnumerateIssueCommentsAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ => WaitForComments(entered, release));
        var handler = new GitHubFindingTriageHandler(
            aliases,
            service,
            publisher,
            clients,
            new CodeReviewSettings(),
            leases
        );
        Task<bool> Invoke() =>
            handler.HandleAsync(
                "org",
                null,
                "repo",
                "owner/repo",
                2,
                12,
                "/zeeq fp abcde.F1 reason",
                "Alice",
                "MEMBER",
                DateTimeOffset.UtcNow,
                null,
                "created",
                "delivery",
                new ZeeqTraceContext(null, null),
                GitHubCommentReactionTargetKind.IssueComment,
                CancellationToken.None
            );
        var first = Invoke();
        await entered.Task;
        await Assert
            .That(async () => await Invoke())
            .Throws<GitHubCommentLeaseUnavailableException>();
        release.SetResult();
        if (renewalSucceeds)
            await first;
        else
            await Assert
                .That(async () => await first)
                .Throws<GitHubCommentLeaseUnavailableException>();
        await client
            .Received(renewalSucceeds ? 1 : 0)
            .CreateIssueCommentAsync(
                "owner/repo",
                2,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
        await leases
            .Received(1)
            .ReleaseAsync(
                Arg.Any<GitHubCommentLeaseKey>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    private static async IAsyncEnumerable<GitHubCommentCandidate> WaitForComments(
        TaskCompletionSource entered,
        TaskCompletionSource release
    )
    {
        entered.SetResult();
        await release.Task;
        yield break;
    }

    [Test]
    [Arguments("CONTRIBUTOR", true)]
    [Arguments("MEMBER", false)]
    public async Task UnauthorizedCommand_DoesNotWrite(string association, bool aliasExists)
    {
        var aliases = Substitute.For<IActiveGitHubAliasStore>();
        if (aliasExists)
            aliases
                .FindActiveGitHubAliasAsync("org", "alice", Arg.Any<CancellationToken>())
                .Returns(
                    new UserAlias
                    {
                        Id = "alias",
                        OrganizationId = "org",
                        UserId = "user",
                        Kind = UserAliasKind.GitHub,
                        DisplayValue = "Alice",
                        NormalizedValue = "alice",
                    }
                );
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var publisher = Substitute.For<IZeeqMessagePublisher>();
        var clients = Substitute.For<IGitHubCommentClientFactory>();
        var client = Substitute.For<IGitHubCommentClient>();
        clients.CreateForOrganizationAsync("org", Arg.Any<CancellationToken>()).Returns(client);
        client
            .EnumerateIssueCommentsAsync(
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(EmptyComments());
        var handler = new GitHubFindingTriageHandler(
            aliases,
            service,
            publisher,
            clients,
            new CodeReviewSettings(),
            CreateLeases()
        );
        await handler.HandleAsync(
            "org",
            null,
            "repo",
            "owner/repo",
            2,
            12,
            "/zeeq fp abcde.F1 reason",
            "Alice",
            association,
            DateTimeOffset.UtcNow,
            null,
            "created",
            "delivery",
            new ZeeqTraceContext(null, null),
            GitHubCommentReactionTargetKind.IssueComment,
            CancellationToken.None
        );
        await service
            .DidNotReceive()
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
        await publisher
            .Received(1)
            .PublishAsync(
                Arg.Is<GitHubCommentReactionRequested>(x =>
                    x.ReactionContent == GitHubCommentReactionContent.Confused
                ),
                Arg.Any<CancellationToken>()
            );
    }

    private static IGitHubCommentLeaseStore CreateLeases()
    {
        var leases = Substitute.For<IGitHubCommentLeaseStore>();
        leases
            .RenewAsync(
                Arg.Any<GitHubCommentLeaseKey>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        leases
            .TryAcquireAsync(
                Arg.Any<GitHubCommentLeaseKey>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(true);
        return leases;
    }

    private static async IAsyncEnumerable<GitHubCommentCandidate> EmptyComments()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Test]
    public async Task AuthorizedCommand_UsesEventTimestampAndRendersLatest()
    {
        var aliases = Substitute.For<IActiveGitHubAliasStore>();
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var publisher = Substitute.For<IZeeqMessagePublisher>();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        aliases
            .FindActiveGitHubAliasAsync("org", "alice", Arg.Any<CancellationToken>())
            .Returns(
                new UserAlias
                {
                    Id = "alias",
                    OrganizationId = "org",
                    UserId = "user",
                    Kind = UserAliasKind.GitHub,
                    DisplayValue = "Alice",
                    NormalizedValue = "alice",
                }
            );
        var row = new CodeReviewFindingTriage
        {
            Id = "triage",
            OrganizationId = "org",
            OwnerQualifiedRepoName = "owner/repo",
            SourceReviewId = "cr_abcde",
            SourceReviewCreatedAtUtc = timestamp,
            OccurrenceKey = "F1",
            SemanticId = "some-valid-finding",
            Facet = "Security",
            FindingSummary = "Finding",
        };
        service
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new FindingTriageOutcome.Applied(row));
        var handler = new GitHubFindingTriageHandler(
            aliases,
            service,
            publisher,
            Substitute.For<IGitHubCommentClientFactory>(),
            new CodeReviewSettings(),
            CreateLeases()
        );
        var handled = await handler.HandleAsync(
            "org",
            null,
            "repo",
            "owner/repo",
            2,
            12,
            "/zeeq fp abcde.F1 validated",
            "@Alice",
            "MEMBER",
            timestamp,
            "https://example.test/comment",
            "created",
            "delivery",
            new ZeeqTraceContext(null, null),
            GitHubCommentReactionTargetKind.IssueComment,
            CancellationToken.None
        );
        await Assert.That(handled).IsTrue();
        await service
            .Received(1)
            .TriageAsync(
                Arg.Is<FindingTriageRequest>(x =>
                    x.ActorUserId == "user"
                    && x.GitHubAliasId == "alias"
                    && x.SourceEventAtUtc == timestamp
                    && x.Disposition == CodeReviewFindingDisposition.FalsePositive
                ),
                Arg.Any<CancellationToken>()
            );
        await publisher
            .Received(1)
            .PublishAsync(
                Arg.Is<GitHubCommentWriteRequested>(x => x.RenderLatestForPullRequest),
                Arg.Any<CancellationToken>()
            );
        await publisher
            .Received(1)
            .PublishAsync(
                Arg.Is<GitHubCommentReactionRequested>(x =>
                    x.ReactionContent == GitHubCommentReactionContent.PlusOne
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task BotCommand_IsSilent()
    {
        var aliases = Substitute.For<IActiveGitHubAliasStore>();
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var publisher = Substitute.For<IZeeqMessagePublisher>();
        var handler = new GitHubFindingTriageHandler(
            aliases,
            service,
            publisher,
            Substitute.For<IGitHubCommentClientFactory>(),
            new CodeReviewSettings(),
            CreateLeases()
        );
        var handled = await handler.HandleAsync(
            "org",
            null,
            "repo",
            "owner/repo",
            2,
            12,
            "/zeeq clear abcde.F1",
            "robot[bot]",
            "MEMBER",
            DateTimeOffset.UtcNow,
            null,
            "created",
            "delivery",
            new ZeeqTraceContext(null, null),
            GitHubCommentReactionTargetKind.IssueComment,
            CancellationToken.None
        );
        await Assert.That(handled).IsTrue();
        await aliases
            .DidNotReceive()
            .FindActiveGitHubAliasAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
        await service
            .DidNotReceive()
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
    }
}
