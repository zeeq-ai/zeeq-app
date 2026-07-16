using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class GitHubCommentWriteRequestedHandlerTests
{
    [Test]
    public async Task HandleAsync_WhenLeaseUnavailable_ThrowsBeforeGitHubWork()
    {
        var fixture = HandlerFixture.Create(
            new GitHubCommentWriteOptions { LeaseAcquireTimeout = TimeSpan.Zero }
        );
        fixture.Leases.AllowAcquire = false;

        await Assert
            .That(async () => await fixture.Handler.HandleAsync(Message(), CancellationToken.None))
            .Throws<GitHubCommentLeaseUnavailableException>();

        await fixture
            .CommentClients.DidNotReceiveWithAnyArgs()
            .CreateForOrganizationAsync(default!, default);
        await Assert.That(fixture.Leases.ReleaseCalls).IsEmpty();
    }

    [Test]
    public async Task HandleAsync_WhenLeaseBecomesAvailable_WaitsThenWritesComment()
    {
        var fixture = HandlerFixture.Create(
            new GitHubCommentWriteOptions
            {
                LeaseAcquireTimeout = TimeSpan.FromMilliseconds(100),
                LeaseAcquireRetryDelay = TimeSpan.FromMilliseconds(1),
            }
        );
        fixture.Leases.RemainingAcquireFailures = 1;

        await fixture.Handler.HandleAsync(Message(), CancellationToken.None);

        await Assert.That(fixture.Leases.AcquireCalls).IsGreaterThanOrEqualTo(2);
        await fixture
            .Writer.Received(1)
            .UpsertAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                null,
                "rendered body",
                Arg.Any<CancellationToken>()
            );
        await Assert.That(fixture.Leases.ReleaseCalls).Count().IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithNewComment_RendersEmptyDomWritesAndPersistsAnchor()
    {
        var fixture = HandlerFixture.Create();
        fixture
            .Renderer.Render(
                "queued",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<CodeReviewCommentRenderContext>(context => context.Review == null),
                Arg.Any<GitHubCommentDom>()
            )
            .Returns("rendered body");
        fixture
            .Writer.UpsertAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                null,
                "rendered body",
                Arg.Any<CancellationToken>()
            )
            .Returns(101L);

        await fixture.Handler.HandleAsync(Message(), CancellationToken.None);

        fixture
            .Renderer.Received(1)
            .Render(
                "queued",
                Arg.Is<IReadOnlyList<string>>(clear =>
                    clear.Contains(GitHubCommentMarkers.PullRequestFindings)
                ),
                Arg.Is<CodeReviewCommentRenderContext>(context => context.Review == null),
                Arg.Is<GitHubCommentDom>(dom => dom.IsEmpty && dom.Target == Target())
            );
        await fixture
            .Anchors.Received(1)
            .UpsertResolvedAsync(Target(), "zeeq-ai/zeeq", 101L, Arg.Any<CancellationToken>());
        await Assert.That(fixture.Leases.ReleaseCalls).Count().IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WithResolvedComment_RepairsAnchorBeforeWriting()
    {
        var fixture = HandlerFixture.Create();
        fixture.Anchors.FindAsync(Target(), Arg.Any<CancellationToken>()).Returns(Anchor(null));
        fixture
            .Resolver.ResolveAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                null,
                Arg.Any<CancellationToken>()
            )
            .Returns(new GitHubCommentResolution(202L, GitHubCommentDom.Empty(Target())));
        fixture
            .Renderer.Render(
                "queued",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<CodeReviewCommentRenderContext>(context => context.Review == null),
                Arg.Any<GitHubCommentDom>()
            )
            .Returns("rendered body");
        fixture
            .Writer.UpsertAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                202L,
                "rendered body",
                Arg.Any<CancellationToken>()
            )
            .Returns(202L);

        await fixture.Handler.HandleAsync(Message(), CancellationToken.None);

        await fixture
            .Anchors.Received(2)
            .UpsertResolvedAsync(Target(), "zeeq-ai/zeeq", 202L, Arg.Any<CancellationToken>());
        await fixture
            .Writer.Received(1)
            .UpsertAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                202L,
                "rendered body",
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_WithReviewReference_LoadsReviewForRenderer()
    {
        var fixture = HandlerFixture.Create();
        var review = ReviewRecord();
        review.FindingsStorageUri = fixture.Artifacts.StorageUri;
        fixture.Artifacts.StoredXml = SampleFindingsXml();
        fixture
            .CodeReviews.FindAsync(review.Id, review.CreatedAtUtc, Arg.Any<CancellationToken>())
            .Returns(review);
        fixture
            .Renderer.Render(
                "stub_review_completed",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<CodeReviewCommentRenderContext>(context =>
                    context.Review == review
                    && context.FindingsXml == fixture.Artifacts.StoredXml
                    && context.Findings != null
                    && context.FindingsLoadError == null
                    && context.ActionLinks.RequestReviewUrl != null
                ),
                Arg.Any<GitHubCommentDom>()
            )
            .Returns("completed body");
        fixture
            .Writer.UpsertAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                null,
                "completed body",
                Arg.Any<CancellationToken>()
            )
            .Returns(303L);

        await fixture.Handler.HandleAsync(
            Message(
                kind: "stub_review_completed",
                codeReviewRecordId: review.Id,
                codeReviewCreatedAtUtc: review.CreatedAtUtc
            ),
            CancellationToken.None
        );

        await fixture
            .CodeReviews.Received(1)
            .FindAsync(review.Id, review.CreatedAtUtc, Arg.Any<CancellationToken>());
        fixture
            .Renderer.Received(1)
            .Render(
                "stub_review_completed",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<CodeReviewCommentRenderContext>(context =>
                    context.Review == review
                    && context.FindingsXml == fixture.Artifacts.StoredXml
                    && context.Findings != null
                    && context.FindingsLoadError == null
                ),
                Arg.Any<GitHubCommentDom>()
            );
    }

    [Test]
    public async Task HandleAsync_WithNoAgentsActivatedReference_PassesRequestAgainLink()
    {
        var fixture = HandlerFixture.Create();
        var review = ReviewRecord();
        review.FindingsStorageUri = fixture.Artifacts.StorageUri;
        fixture.Artifacts.StoredXml = SampleNoAgentsFindingsXml();
        fixture
            .CodeReviews.FindAsync(review.Id, review.CreatedAtUtc, Arg.Any<CancellationToken>())
            .Returns(review);
        fixture
            .Renderer.Render(
                "no_agents_activated",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<CodeReviewCommentRenderContext>(context =>
                    context.Review == review
                    && context.Findings != null
                    && context.Findings.NoAgentsActivated
                    && context.FindingsLoadError == null
                    && context.ActionLinks.RequestReviewUrl != null
                ),
                Arg.Any<GitHubCommentDom>()
            )
            .Returns("no agents body");
        fixture
            .Writer.UpsertAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                null,
                "no agents body",
                Arg.Any<CancellationToken>()
            )
            .Returns(303L);

        await fixture.Handler.HandleAsync(
            Message(
                kind: "no_agents_activated",
                codeReviewRecordId: review.Id,
                codeReviewCreatedAtUtc: review.CreatedAtUtc
            ),
            CancellationToken.None
        );

        fixture
            .Renderer.Received(1)
            .Render(
                "no_agents_activated",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<CodeReviewCommentRenderContext>(context =>
                    context.ActionLinks.RequestReviewUrl != null
                ),
                Arg.Any<GitHubCommentDom>()
            );
    }

    [Test]
    public async Task HandleAsync_WhenReferencedReviewIsMissing_ThrowsBeforeRenderingAndReleasesLease()
    {
        var fixture = HandlerFixture.Create();
        var review = ReviewRecord();

        await Assert
            .That(async () =>
                await fixture.Handler.HandleAsync(
                    Message(
                        kind: "stub_review_completed",
                        codeReviewRecordId: review.Id,
                        codeReviewCreatedAtUtc: review.CreatedAtUtc
                    ),
                    CancellationToken.None
                )
            )
            .Throws<InvalidOperationException>()
            .WithMessageContaining("Referenced code review was not found");

        fixture.Renderer.DidNotReceiveWithAnyArgs().Render(default!, default!, default!, default!);
        await fixture
            .Writer.DidNotReceiveWithAnyArgs()
            .UpsertAsync(default!, default!, default!, default, default!, default);
        await Assert.That(fixture.Leases.ReleaseCalls).Count().IsEqualTo(1);
    }

    [Test]
    public async Task HandleAsync_WhenRenewalFails_CancelsWorkAndReleasesLease()
    {
        var fixture = HandlerFixture.Create(
            new GitHubCommentWriteOptions { LeaseDuration = TimeSpan.FromMilliseconds(20) }
        );
        fixture.Leases.RenewResult = false;
        fixture
            .Renderer.Render(
                "queued",
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Is<CodeReviewCommentRenderContext>(context => context.Review == null),
                Arg.Any<GitHubCommentDom>()
            )
            .Returns("slow body");
        fixture
            .Writer.UpsertAsync(
                fixture.CommentClient,
                Target(),
                "zeeq-ai/zeeq",
                null,
                "slow body",
                Arg.Any<CancellationToken>()
            )
            .Returns(call => CompleteWhenCanceledAsync(call.ArgAt<CancellationToken>(5)));

        await Assert
            .That(async () => await fixture.Handler.HandleAsync(Message(), CancellationToken.None))
            .Throws<GitHubCommentLeaseLostException>();

        await Assert.That(fixture.Leases.RenewCalls).IsGreaterThanOrEqualTo(1);
        await Assert.That(fixture.Leases.ReleaseCalls).Count().IsEqualTo(1);
    }

    private static GitHubCommentWriteRequested Message(
        string kind = "queued",
        string? codeReviewRecordId = null,
        DateTimeOffset? codeReviewCreatedAtUtc = null
    ) =>
        new()
        {
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            PullRequestNumber = 42,
            Target = Target(),
            Kind = kind,
            Clear = [GitHubCommentMarkers.PullRequestFindings],
            CodeReviewRecordId = codeReviewRecordId,
            CodeReviewCreatedAtUtc = codeReviewCreatedAtUtc,
            SignalId = "signal_123",
            TraceContext = new ZeeqTraceContext(null, null),
        };

    private static string SampleFindingsXml() =>
        CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument
            {
                Reviews =
                [
                    new()
                    {
                        Facet = "Structural",
                        Agent = "Structural Reviewer",
                        Summary = "LGTM overall.",
                        Details = "One structural note.",
                        Findings =
                        [
                            new()
                            {
                                Level = CodeReviewFindingLevel.Comment,
                                File = "src/file.cs",
                                Line = 12,
                                Side = "RIGHT",
                                Summary = "A useful finding",
                                Details = "Finding body.",
                            },
                        ],
                    },
                ],
            }
        );

    private static string SampleNoAgentsFindingsXml() =>
        CodeReviewXmlOutputValidator.Serialize(
            new CodeReviewOutputDocument { NoAgentsActivated = true }
        );

    private static GitHubCommentTargetSelector Target() =>
        new(
            OrganizationId: "org_123",
            RepositoryId: "repo_123",
            PullRequestNumber: 42,
            Kind: GitHubCommentTargetKind.PullRequestSummary,
            ScopeKey: GitHubCommentMarkers.PullRequestSummaryScopeKey
        );

    private static GitHubCommentAnchor Anchor(long? commentId) =>
        new()
        {
            TargetKey = Target().ToStorageKey(),
            OrganizationId = "org_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            PullRequestNumber = 42,
            Kind = GitHubCommentTargetKind.PullRequestSummary,
            ScopeKey = GitHubCommentMarkers.PullRequestSummaryScopeKey,
            GitHubCommentId = commentId,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static CodeReviewRecord ReviewRecord()
    {
        var now = DateTimeOffset.UtcNow;
        return new()
        {
            Id = "cr_123",
            OrganizationId = "org_123",
            TeamId = "team_123",
            PullRequestRecordId = "pr_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            PullRequestNumber = 42,
            Branch = "feature/comments",
            Title = "Render comments",
            AuthorLogin = "octocat",
            Status = CodeReviewStatus.Completed,
            RequestOrigin = CodeReviewRequestOrigin.RepositoryWebhook,
            RemainingReviewBudget = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static Task<long> CompleteWhenCanceledAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<long>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        cancellationToken.Register(() => tcs.SetCanceled(cancellationToken));
        return tcs.Task;
    }

    private sealed class HandlerFixture
    {
        private HandlerFixture() { }

        public TestGitHubCommentLeaseStore Leases { get; } = new();
        public IGitHubCommentAnchorStore Anchors { get; } =
            Substitute.For<IGitHubCommentAnchorStore>();
        public IGitHubCommentClientFactory CommentClients { get; } =
            Substitute.For<IGitHubCommentClientFactory>();
        public IGitHubCommentClient CommentClient { get; } = Substitute.For<IGitHubCommentClient>();
        public IGitHubCommentResolver Resolver { get; } = Substitute.For<IGitHubCommentResolver>();
        public IGitHubCommentDomRenderer Renderer { get; } =
            Substitute.For<IGitHubCommentDomRenderer>();
        public ICodeReviewRecordStore CodeReviews { get; } =
            Substitute.For<ICodeReviewRecordStore>();
        public TestCodeReviewArtifactStore Artifacts { get; } = new();
        public IGitHubCommentWriter Writer { get; } = Substitute.For<IGitHubCommentWriter>();
        public GitHubCommentWriteRequestedHandler Handler { get; private set; } = null!;

        public static HandlerFixture Create(GitHubCommentWriteOptions? options = null)
        {
            var fixture = new HandlerFixture();
            var deadLetters = Substitute.For<IDeadLetterWriter>();
            var settings = new AppSettings
            {
                Http = new HttpSettings { FrontendBaseUri = "http://zeeq-web.test" },
                CodeReview = new CodeReviewSettings
                {
                    ReviewRequestLinkEncryptionKey = "test-review-request-key",
                    ReviewRequestLinkValidityDays = 7,
                    DefaultReviewBudget = 10,
                },
            };
            var linkFactory = new CodeReviewRequestLinkFactory(
                Options.Create(settings),
                new CodeReviewRequestTokenProtector(settings.CodeReview)
            );

            fixture
                .Anchors.FindAsync(Target(), Arg.Any<CancellationToken>())
                .Returns((GitHubCommentAnchor?)null);
            fixture
                .CommentClients.CreateForOrganizationAsync("org_123", Arg.Any<CancellationToken>())
                .Returns(fixture.CommentClient);
            fixture
                .Resolver.ResolveAsync(
                    fixture.CommentClient,
                    Target(),
                    "zeeq-ai/zeeq",
                    null,
                    Arg.Any<CancellationToken>()
                )
                .Returns((GitHubCommentResolution?)null);
            fixture
                .Renderer.Render(
                    "queued",
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Is<CodeReviewCommentRenderContext>(context => context.Review == null),
                    Arg.Any<GitHubCommentDom>()
                )
                .Returns("rendered body");
            fixture
                .Writer.UpsertAsync(
                    fixture.CommentClient,
                    Target(),
                    "zeeq-ai/zeeq",
                    null,
                    "rendered body",
                    Arg.Any<CancellationToken>()
                )
                .Returns(101L);
            fixture
                .Anchors.UpsertResolvedAsync(
                    Target(),
                    "zeeq-ai/zeeq",
                    Arg.Any<long>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call => Anchor(call.ArgAt<long>(2)));

            fixture.Handler = new GitHubCommentWriteRequestedHandler(
                deadLetters,
                fixture.Leases,
                fixture.Anchors,
                fixture.CommentClients,
                fixture.Resolver,
                fixture.Renderer,
                fixture.CodeReviews,
                fixture.Artifacts,
                new CodeReviewXmlOutputValidator(),
                linkFactory,
                fixture.Writer,
                options ?? new GitHubCommentWriteOptions(),
                NullLogger<GitHubCommentWriteRequestedHandler>.Instance
            );

            return fixture;
        }
    }

    private sealed class TestCodeReviewArtifactStore : ICodeReviewArtifactStore
    {
        public string StorageUri { get; } =
            "postgres://code-review-findings/org_123/cr_123/202606250000000000000.xml";

        public string StoredXml { get; set; } = string.Empty;

        public Task<string> WriteFindingsAsync(
            CodeReviewRecord review,
            Stream findings,
            string contentType,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<Stream> OpenFindingsAsync(
            string findingsStorageUri,
            CancellationToken cancellationToken
        ) => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(StoredXml)));

        public Task CopyFindingsToAsync(
            string findingsStorageUri,
            Stream destination,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class TestGitHubCommentLeaseStore : IGitHubCommentLeaseStore
    {
        public bool AllowAcquire { get; set; } = true;
        public int RemainingAcquireFailures { get; set; }
        public int AcquireCalls { get; private set; }
        public bool RenewResult { get; set; } = true;
        public int RenewCalls { get; private set; }
        public List<(GitHubCommentLeaseKey Key, string WorkerId)> ReleaseCalls { get; } = [];

        public Task<bool> TryAcquireAsync(
            GitHubCommentLeaseKey key,
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken
        )
        {
            AcquireCalls++;

            if (RemainingAcquireFailures > 0)
            {
                RemainingAcquireFailures--;
                return Task.FromResult(false);
            }

            return Task.FromResult(AllowAcquire);
        }

        public Task<bool> RenewAsync(
            GitHubCommentLeaseKey key,
            string workerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken
        )
        {
            RenewCalls++;
            return Task.FromResult(RenewResult);
        }

        public Task ReleaseAsync(
            GitHubCommentLeaseKey key,
            string workerId,
            CancellationToken cancellationToken
        )
        {
            ReleaseCalls.Add((key, workerId));
            return Task.CompletedTask;
        }
    }
}
