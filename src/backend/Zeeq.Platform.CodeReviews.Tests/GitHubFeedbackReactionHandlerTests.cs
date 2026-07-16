using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class GitHubFeedbackReactionHandlerTests
{
    [Test]
    public async Task IssueCommentHandler_WithCreatedUserComment_PublishesImmediateReaction()
    {
        var fixture = FeedbackFixture.Create();

        await fixture.IssueHandler.HandleAsync(IssueCommentMessage(), CancellationToken.None);

        var published = fixture.Published.OfType<GitHubCommentReactionRequested>().Single();

        await Assert.That(published.OrganizationId).IsEqualTo("org_123");
        await Assert.That(published.RepositoryId).IsEqualTo("repo_123");
        await Assert.That(published.OwnerQualifiedRepoName).IsEqualTo("zeeq-ai/zeeq");
        await Assert.That(published.PullRequestNumber).IsEqualTo(42);
        await Assert
            .That(published.Target.Kind)
            .IsEqualTo(GitHubCommentReactionTargetKind.IssueComment);
        await Assert.That(published.Target.CommentId).IsEqualTo(9001);
        await Assert
            .That(published.ReactionContent)
            .IsEqualTo(GitHubCommentReactionContent.PlusOne);
        await Assert.That(published.SignalId).IsEqualTo("delivery_123");
    }

    [Test]
    public async Task IssueCommentHandler_WithEditedComment_DoesNotPublishReaction()
    {
        var fixture = FeedbackFixture.Create();

        await fixture.IssueHandler.HandleAsync(
            IssueCommentMessage(action: "edited"),
            CancellationToken.None
        );

        await Assert.That(fixture.Published).IsEmpty();
    }

    [Test]
    public async Task IssueCommentHandler_WithBotAuthoredComment_DoesNotPublishReaction()
    {
        var fixture = FeedbackFixture.Create();

        await fixture.IssueHandler.HandleAsync(
            IssueCommentMessage(authorLogin: "zeeq-app[bot]"),
            CancellationToken.None
        );

        await Assert.That(fixture.Published).IsEmpty();
    }

    [Test]
    public async Task IssueCommentHandler_WithNonCommandComment_DoesNotPublishReaction()
    {
        var fixture = FeedbackFixture.Create();

        await fixture.IssueHandler.HandleAsync(
            IssueCommentMessage(commentBody: "This is normal PR discussion."),
            CancellationToken.None
        );

        await Assert.That(fixture.Published).IsEmpty();
    }

    [Test]
    public async Task ReviewCommentHandler_WithCreatedUserComment_PublishesImmediateReaction()
    {
        var fixture = FeedbackFixture.Create();

        await fixture.ReviewHandler.HandleAsync(ReviewCommentMessage(), CancellationToken.None);

        var published = fixture.Published.OfType<GitHubCommentReactionRequested>().Single();

        await Assert
            .That(published.Target.Kind)
            .IsEqualTo(GitHubCommentReactionTargetKind.PullRequestReviewComment);
        await Assert.That(published.Target.CommentId).IsEqualTo(7001);
        await Assert.That(published.PullRequestNumber).IsEqualTo(42);
        await Assert
            .That(published.ReactionContent)
            .IsEqualTo(GitHubCommentReactionContent.PlusOne);
        await Assert.That(published.SignalId).IsEqualTo("delivery_123");
    }

    [Test]
    public async Task ReviewCommentHandler_WithNonCommandComment_DoesNotPublishReaction()
    {
        var fixture = FeedbackFixture.Create();

        await fixture.ReviewHandler.HandleAsync(
            ReviewCommentMessage(commentBody: "Nit: rename this local."),
            CancellationToken.None
        );

        await Assert.That(fixture.Published).IsEmpty();
    }

    [Test]
    [Arguments("/zeeq please review this")]
    [Arguments("   +zeeq")]
    public async Task FeedbackCommandPolicy_WithSupportedCommandToken_ReturnsTrue(
        string commentBody
    )
    {
        var result = GitHubFeedbackCommandPolicy.IsSupportedFeedbackCommand(commentBody);

        await Assert.That(result).IsTrue();
    }

    [Test]
    [Arguments("please +zeeq")]
    [Arguments("`+zeeq`")]
    [Arguments("Can you review this?")]
    [Arguments("")]
    public async Task FeedbackCommandPolicy_WithUnsupportedCommandShape_ReturnsFalse(
        string commentBody
    )
    {
        var result = GitHubFeedbackCommandPolicy.IsSupportedFeedbackCommand(commentBody);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ReactionHandler_WithCreatedOutcome_Completes()
    {
        var fixture = ReactionFixture.Create(GitHubCommentReactionWriteOutcome.Created);

        await fixture.Handler.HandleAsync(ReactionMessage(), CancellationToken.None);

        await fixture
            .Client.Received(1)
            .AddReactionAsync(
                "zeeq-ai/zeeq",
                Arg.Is<GitHubCommentReactionTarget>(target =>
                    target.Kind == GitHubCommentReactionTargetKind.IssueComment
                    && target.CommentId == 9001
                ),
                GitHubCommentReactionContent.PlusOne,
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ReactionHandler_WithAlreadyExistsOutcome_Completes()
    {
        var fixture = ReactionFixture.Create(GitHubCommentReactionWriteOutcome.AlreadyExists);

        await fixture.Handler.HandleAsync(ReactionMessage(), CancellationToken.None);

        await fixture
            .Client.Received(1)
            .AddReactionAsync(
                Arg.Any<string>(),
                Arg.Any<GitHubCommentReactionTarget>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ReactionHandler_WithValidationFailure_CompletesAsNoOp()
    {
        var fixture = ReactionFixture.Create(GitHubCommentReactionWriteOutcome.ValidationFailed);

        await fixture.Handler.HandleAsync(ReactionMessage(), CancellationToken.None);

        await fixture
            .Client.Received(1)
            .AddReactionAsync(
                Arg.Any<string>(),
                Arg.Any<GitHubCommentReactionTarget>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ReactionHandler_WithTransientFailure_ThrowsForRetry()
    {
        var fixture = ReactionFixture.CreateFailure();

        async Task Act() =>
            await fixture.Handler.HandleAsync(ReactionMessage(), CancellationToken.None);

        await Assert.That(Act).Throws<InvalidOperationException>();
    }

    private static GitHubIssueCommentWebhookReceived IssueCommentMessage(
        string action = "created",
        string authorLogin = "octo-user",
        string commentBody = "+zeeq"
    ) =>
        new()
        {
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            GitHubDeliveryId = "delivery_123",
            GitHubEvent = "issue_comment",
            GitHubAction = action,
            GitHubInstallationId = 123,
            TraceContext = EmptyTraceContext(),
            PullRequestNumber = 42,
            IssueNodeId = "issue_node",
            CommentId = 9001,
            CommentNodeId = "comment_node",
            CommentBody = commentBody,
            CommentAuthorLogin = authorLogin,
            CommentHtmlUrl = "https://github.test/comment/9001",
        };

    private static GitHubPullRequestReviewCommentWebhookReceived ReviewCommentMessage(
        string commentBody = "+zeeq"
    ) =>
        new()
        {
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            GitHubDeliveryId = "delivery_123",
            GitHubEvent = "pull_request_review_comment",
            GitHubAction = "created",
            GitHubInstallationId = 123,
            TraceContext = EmptyTraceContext(),
            PullRequestNumber = 42,
            PullRequestNodeId = "pr_node",
            CommentId = 7001,
            CommentNodeId = "review_comment_node",
            CommentBody = commentBody,
            CommentAuthorLogin = "octo-user",
            CommentHtmlUrl = "https://github.test/comment/7001",
            Path = "src/app.cs",
            CommitId = "abc123",
            PullRequestReviewId = 8001,
        };

    private static GitHubCommentReactionRequested ReactionMessage() =>
        new()
        {
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            PullRequestNumber = 42,
            Target = new(GitHubCommentReactionTargetKind.IssueComment, 9001),
            ReactionContent = GitHubCommentReactionContent.PlusOne,
            SignalId = "delivery_123",
            TraceContext = EmptyTraceContext(),
        };

    private static ZeeqTraceContext EmptyTraceContext() => new(null, null);

    private sealed class FeedbackFixture
    {
        private FeedbackFixture() { }

        public List<IRequest> Published { get; } = [];
        public GitHubIssueCommentWebhookReceivedHandler IssueHandler { get; private set; } = null!;
        public GitHubPullRequestReviewCommentWebhookReceivedHandler ReviewHandler
        {
            get;
            private set;
        } = null!;

        public static FeedbackFixture Create()
        {
            var fixture = new FeedbackFixture();
            var publisher = Substitute.For<IZeeqMessagePublisher>();
            publisher
                .PublishAsync(
                    Arg.Do<GitHubCommentReactionRequested>(message =>
                        fixture.Published.Add(message)
                    ),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.CompletedTask);
            var deadLetters = Substitute.For<IDeadLetterWriter>();
            var settings = new CodeReviewSettings { AgentIdentity = "zeeq-code-review-agent" };

            fixture.IssueHandler = new GitHubIssueCommentWebhookReceivedHandler(
                deadLetters,
                publisher,
                settings,
                NullLogger<GitHubIssueCommentWebhookReceivedHandler>.Instance
            );
            fixture.ReviewHandler = new GitHubPullRequestReviewCommentWebhookReceivedHandler(
                deadLetters,
                publisher,
                settings,
                NullLogger<GitHubPullRequestReviewCommentWebhookReceivedHandler>.Instance
            );

            return fixture;
        }
    }

    private sealed class ReactionFixture
    {
        private ReactionFixture() { }

        public IGitHubCommentReactionClient Client { get; private set; } = null!;
        public GitHubCommentReactionRequestedHandler Handler { get; private set; } = null!;

        public static ReactionFixture Create(GitHubCommentReactionWriteOutcome outcome)
        {
            var fixture = new ReactionFixture();
            var factory = Substitute.For<IGitHubCommentReactionClientFactory>();
            fixture.Client = Substitute.For<IGitHubCommentReactionClient>();
            factory
                .CreateForOrganizationAsync("org_123", Arg.Any<CancellationToken>())
                .Returns(fixture.Client);
            fixture
                .Client.AddReactionAsync(
                    Arg.Any<string>(),
                    Arg.Any<GitHubCommentReactionTarget>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(outcome);

            fixture.Handler = new GitHubCommentReactionRequestedHandler(
                Substitute.For<IDeadLetterWriter>(),
                factory,
                NullLogger<GitHubCommentReactionRequestedHandler>.Instance
            );

            return fixture;
        }

        public static ReactionFixture CreateFailure()
        {
            var fixture = Create(GitHubCommentReactionWriteOutcome.Created);
            fixture
                .Client.AddReactionAsync(
                    Arg.Any<string>(),
                    Arg.Any<GitHubCommentReactionTarget>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns<Task<GitHubCommentReactionWriteOutcome>>(_ =>
                    throw new InvalidOperationException("github unavailable")
                );

            return fixture;
        }
    }
}
