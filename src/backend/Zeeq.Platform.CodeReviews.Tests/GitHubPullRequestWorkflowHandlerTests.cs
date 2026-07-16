using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class GitHubPullRequestWorkflowHandlerTests
{
    [Test]
    public async Task PullRequestHandler_WithReadyPullRequest_CreatesReviewAndPublishesFollowUpWork()
    {
        using var fixture = WorkflowFixture.Create();

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        var pullRequest = fixture.PullRequests.Records.Single();
        var review = fixture.CodeReviews.Records.Single();
        var immediate = fixture.Published.OfType<GitHubCommentWriteRequested>().Single();
        var run = fixture.Published.OfType<CodeReviewRunRequested>().Single();

        await Assert.That(pullRequest.State).IsEqualTo(PullRequestState.Open);
        await Assert.That(review.Status).IsEqualTo(CodeReviewStatus.Pending);
        await Assert.That(review.RemainingReviewBudget).IsEqualTo(10);
        await Assert.That(fixture.ActiveLocks.Locks).Count().IsEqualTo(1);
        await Assert.That(immediate.Kind).IsEqualTo("queued");
        await Assert.That(immediate.CodeReviewRecordId).IsEqualTo(review.Id);
        await Assert.That(immediate.Target).IsEqualTo(PullRequestSummaryTarget());
        await Assert.That(immediate.Clear).Contains(GitHubCommentMarkers.PullRequestFindings);
        await Assert.That(run.CodeReviewRecordId).IsEqualTo(review.Id);
        await Assert.That(run.PullRequestRecordId).IsEqualTo(pullRequest.Id);
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).IsEquivalentTo(["delivery_123"]);
    }

    [Test]
    public async Task PullRequestHandler_WhenRepositoryMappingIsStale_DoesNotPersistPullRequestOrReview()
    {
        using var fixture = WorkflowFixture.Create();
        fixture.Repositories.ActiveRepository = null;

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        await Assert.That(fixture.PullRequests.Records).IsEmpty();
        await Assert.That(fixture.CodeReviews.Records).IsEmpty();
        await Assert.That(fixture.ActiveLocks.Locks).IsEmpty();
        await Assert.That(fixture.Published).IsEmpty();
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).IsEquivalentTo(["delivery_123"]);
    }

    [Test]
    public async Task PullRequestHandler_WithOrganizationScopedRepositoryMapping_AcceptsTeamScopedMessage()
    {
        using var fixture = WorkflowFixture.Create();
        fixture.Repositories.ActiveRepository!.TeamId = null;

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        var review = fixture.CodeReviews.Records.Single();
        var run = fixture.Published.OfType<CodeReviewRunRequested>().Single();

        await Assert.That(review.TeamId).IsEqualTo("team_123");
        await Assert.That(run.TeamId).IsEqualTo("team_123");
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).IsEquivalentTo(["delivery_123"]);
    }

    [Test]
    public async Task PullRequestHandler_WithDuplicateDelivery_DoesNotCreateWorkflowWork()
    {
        using var fixture = WorkflowFixture.Create();
        fixture.Deliveries.NextClaimResult = WebhookDeliveryClaimResult.AlreadyProcessed;

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        await Assert.That(fixture.PullRequests.Records).IsEmpty();
        await Assert.That(fixture.CodeReviews.Records).IsEmpty();
        await Assert.That(fixture.ActiveLocks.Locks).IsEmpty();
        await Assert.That(fixture.Published).IsEmpty();
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).IsEmpty();
    }

    [Test]
    public async Task PullRequestHandler_WhenTelemetryLinkingFails_StillAcknowledgesDelivery()
    {
        using var fixture = WorkflowFixture.Create();
        fixture
            .TelemetryLinker.LinkAsync(Arg.Any<PullRequestRecord>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<int>(new InvalidOperationException("telemetry unavailable"))
            );

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).IsEquivalentTo(["delivery_123"]);
        await Assert.That(fixture.CodeReviews.Records).Count().IsEqualTo(1);
    }

    [Test]
    public async Task PullRequestHandler_WithDraftPullRequest_StoresPullRequestAndPublishesDraftPrompt()
    {
        using var fixture = WorkflowFixture.Create();
        var message = PullRequestMessage(isDraft: true);

        await fixture.PullRequestHandler.HandleAsync(message, CancellationToken.None);

        var pullRequest = fixture.PullRequests.Records.Single();
        var immediate = fixture.Published.OfType<GitHubCommentWriteRequested>().Single();

        await Assert.That(pullRequest.IsDraft).IsTrue();
        await Assert.That(immediate.Kind).IsEqualTo("draft_prompt");
        await Assert.That(immediate.CodeReviewRecordId).IsNull();
        await Assert.That(immediate.Target).IsEqualTo(PullRequestSummaryTarget());
        await Assert
            .That(immediate.Clear)
            .IsEquivalentTo([
                GitHubCommentMarkers.PullRequestStatus,
                GitHubCommentMarkers.PullRequestFindings,
                GitHubCommentMarkers.PullRequestEvidence,
            ]);
        await Assert.That(fixture.CodeReviews.Records).IsEmpty();
        await Assert.That(fixture.Published.OfType<CodeReviewRunRequested>()).IsEmpty();
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).IsEquivalentTo(["delivery_123"]);
    }

    [Test]
    public async Task PullRequestHandler_WithClosedPullRequest_StoresPullRequestWithoutReplacingLastReviewComment()
    {
        using var fixture = WorkflowFixture.Create();

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        var pullRequest = fixture.PullRequests.Records.Single();
        var existingReview = fixture.CodeReviews.Records.Single();
        existingReview.Status = CodeReviewStatus.Completed;
        fixture.ActiveLocks.Locks.Clear();
        fixture.Published.Clear();

        await fixture.PullRequestHandler.HandleAsync(
            PullRequestMessage(action: "closed", state: "closed", deliveryId: "delivery_456"),
            CancellationToken.None
        );

        await Assert.That(fixture.PullRequests.Records).Count().IsEqualTo(1);
        await Assert.That(fixture.PullRequests.Records.Single().Id).IsEqualTo(pullRequest.Id);
        await Assert
            .That(fixture.PullRequests.Records.Single().State)
            .IsEqualTo(PullRequestState.Closed);
        await Assert.That(fixture.CodeReviews.Records).IsEquivalentTo([existingReview]);
        await Assert.That(fixture.Published).IsEmpty();
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).Contains("delivery_456");
    }

    [Test]
    public async Task PullRequestHandler_WithActiveReview_PublishesAlreadyRunningAcknowledgementOnly()
    {
        using var fixture = WorkflowFixture.Create();
        fixture.ActiveLocks.AllowAcquire = false;

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        var immediate = fixture.Published.OfType<GitHubCommentWriteRequested>().Single();

        await Assert.That(fixture.PullRequests.Records).Count().IsEqualTo(1);
        await Assert.That(fixture.CodeReviews.Records).IsEmpty();
        await Assert.That(fixture.ActiveLocks.Locks).IsEmpty();
        await Assert.That(immediate.Kind).IsEqualTo("already_running");
        await Assert.That(fixture.Published.OfType<CodeReviewRunRequested>()).IsEmpty();
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).IsEquivalentTo(["delivery_123"]);
    }

    [Test]
    public async Task PullRequestHandler_WhenReviewBudgetIsExhausted_PublishesAllowanceExhaustedOnly()
    {
        using var fixture = WorkflowFixture.Create();

        await fixture.PullRequestHandler.HandleAsync(PullRequestMessage(), CancellationToken.None);

        var pullRequest = fixture.PullRequests.Records.Single();
        var existingReview = fixture.CodeReviews.Records.Single();
        existingReview.Status = CodeReviewStatus.Completed;
        existingReview.RemainingReviewBudget = 0;
        fixture.ActiveLocks.Locks.Clear();
        fixture.Published.Clear();

        await fixture.PullRequestHandler.HandleAsync(
            PullRequestMessage(action: "synchronize", deliveryId: "delivery_456"),
            CancellationToken.None
        );

        var immediate = fixture.Published.OfType<GitHubCommentWriteRequested>().Single();

        await Assert.That(fixture.PullRequests.Records).Count().IsEqualTo(1);
        await Assert.That(fixture.PullRequests.Records.Single().Id).IsEqualTo(pullRequest.Id);
        await Assert.That(fixture.CodeReviews.Records).Count().IsEqualTo(1);
        await Assert.That(immediate.Kind).IsEqualTo("allowance_exhausted");
        await Assert.That(immediate.CodeReviewRecordId).IsEqualTo(existingReview.Id);
        await Assert.That(fixture.Published.OfType<CodeReviewRunRequested>()).IsEmpty();
        await Assert.That(fixture.Deliveries.ProcessedDeliveryIds).Contains("delivery_456");
    }

    [Test]
    public async Task ReviewRunHandler_WithStubRunner_CompletesReviewAndPublishesCommentWriteSignal()
    {
        using var activityListener = new ZeeqActivityListener();
        using var fixture = WorkflowFixture.Create();
        var review = CodeReviewRecord(remainingReviewBudget: 2);
        fixture.CodeReviews.Records.Add(review);
        fixture.ActiveLocks.Locks.Add(
            new ActiveCodeReviewLock
            {
                OrganizationId = review.OrganizationId,
                TeamId = review.TeamId,
                RepositoryId = review.RepositoryId!,
                PullRequestRecordId = review.PullRequestRecordId!,
                PullRequestCreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                CodeReviewRecordId = review.Id,
                CodeReviewCreatedAtUtc = review.CreatedAtUtc,
                Status = CodeReviewStatus.Pending,
                AcquiredAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }
        );

        var message = ReviewRunMessage(review);

        await fixture.ReviewRunHandler.HandleAsync(message, CancellationToken.None);

        var published = fixture.Published.OfType<GitHubCommentWriteRequested>().Single();

        await Assert.That(review.Status).IsEqualTo(CodeReviewStatus.Completed);
        await Assert
            .That(review.FindingsStorageUri)
            .IsEqualTo("postgres://code-review-findings/org_123/review_stub/test.xml");
        await Assert
            .That(review.SourceTelemetryPayload)
            .IsEqualTo(Zeeq.Core.Models.CodeReviewRecord.EmptySourceTelemetryPayload);
        await Assert.That(review.RemainingReviewBudget).IsEqualTo(1);
        await Assert.That(fixture.ActiveLocks.Locks).IsEmpty();
        await Assert.That(fixture.ExecutionLeases.Leases).IsEmpty();
        await fixture.Runner.Received(1).RunAsync(message, review, Arg.Any<CancellationToken>());
        await Assert.That(published.Kind).IsEqualTo("review_completed");
        await Assert.That(published.Target).IsEqualTo(PullRequestSummaryTarget());
        await Assert.That(published.CodeReviewRecordId).IsEqualTo(review.Id);
        await Assert.That(published.CodeReviewCreatedAtUtc).IsEqualTo(review.CreatedAtUtc);
        await Assert.That(published.Clear).Contains(GitHubCommentMarkers.PullRequestFindings);
        await Assert.That(published.SignalId).IsEqualTo(review.Id);
        await Assert.That(fixture.RuntimeStatistics.Runtimes).Count().IsEqualTo(1);

        var runnerCompletedTags = activityListener
            .StoppedActivities.SelectMany(activity => activity.Events)
            .First(activityEvent =>
                activityEvent.Name == "code_review.runner_completed"
                && activityEvent.Tags.Any(tag =>
                    tag.Key == "code_review.id" && Equals(tag.Value, review.Id)
                )
            )
            .Tags.ToDictionary(tag => tag.Key, tag => tag.Value);
        var runtimeMilliseconds = (double)runnerCompletedTags["code_review.runtime_ms"]!;
        var p50Milliseconds = (double)runnerCompletedTags["code_review.runtime.p50_ms"]!;
        var p95Milliseconds = (double)runnerCompletedTags["code_review.runtime.p95_ms"]!;

        await Assert.That(runtimeMilliseconds).IsGreaterThanOrEqualTo(0);
        await Assert.That(p50Milliseconds).IsEqualTo(runtimeMilliseconds);
        await Assert.That(p95Milliseconds).IsEqualTo(runtimeMilliseconds);
    }

    [Test]
    public async Task ReviewRunHandler_WhenRunnerFails_MarksReviewErroredAndReleasesActiveLock()
    {
        var runner = Substitute.For<ICodeReviewRunner>();
        runner
            .RunAsync(
                Arg.Any<CodeReviewRunRequested>(),
                Arg.Any<CodeReviewRecord>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<Task<CodeReviewRunResult>>(_ =>
                throw new InvalidOperationException("runner failed")
            );
        using var fixture = WorkflowFixture.Create(runner);
        var review = CodeReviewRecord();
        review.SourceTelemetryPayload = string.Empty;
        fixture.CodeReviews.Records.Add(review);
        fixture.ActiveLocks.Locks.Add(
            new ActiveCodeReviewLock
            {
                OrganizationId = review.OrganizationId,
                TeamId = review.TeamId,
                RepositoryId = review.RepositoryId!,
                PullRequestRecordId = review.PullRequestRecordId!,
                PullRequestCreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                CodeReviewRecordId = review.Id,
                CodeReviewCreatedAtUtc = review.CreatedAtUtc,
                Status = CodeReviewStatus.Pending,
                AcquiredAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }
        );

        await Assert
            .That(async () =>
                await fixture.ReviewRunHandler.HandleAsync(
                    ReviewRunMessage(review),
                    CancellationToken.None
                )
            )
            .Throws<InvalidOperationException>();

        await Assert.That(review.Status).IsEqualTo(CodeReviewStatus.Errored);
        await Assert
            .That(review.SourceTelemetryPayload)
            .IsEqualTo(Zeeq.Core.Models.CodeReviewRecord.EmptySourceTelemetryPayload);
        await Assert.That(review.FailureMessage).IsEqualTo("runner failed");
        await Assert.That(fixture.ActiveLocks.Locks).IsEmpty();
        await Assert.That(fixture.ExecutionLeases.Leases).IsEmpty();
        await Assert
            .That(fixture.Published.OfType<GitHubCommentWriteRequested>().Single().Kind)
            .IsEqualTo("review_failed");
        await Assert.That(fixture.RuntimeStatistics.Runtimes).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ReviewRunHandler_WhenCompletedCommentPublishFails_RecordsRuntimeOnce()
    {
        using var fixture = WorkflowFixture.Create();
        var review = CodeReviewRecord();
        fixture.CodeReviews.Records.Add(review);
        fixture.ActiveLocks.Locks.Add(ActiveLockFor(review));
        fixture
            .Publisher.PublishAsync(
                Arg.Is<GitHubCommentWriteRequested>(message => message.Kind == "review_completed"),
                Arg.Any<CancellationToken>()
            )
            .Returns<Task>(_ => throw new InvalidOperationException("publish failed"));

        await Assert
            .That(async () =>
                await fixture.ReviewRunHandler.HandleAsync(
                    ReviewRunMessage(review),
                    CancellationToken.None
                )
            )
            .Throws<InvalidOperationException>();

        await Assert.That(fixture.RuntimeStatistics.Runtimes).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ReviewRunHandler_WhenOrganizationCapacityIsFull_WaitsAndRunsWhenCapacityOpens()
    {
        using var fixture = WorkflowFixture.Create();
        fixture.ExecutionLeases.Outcomes.Enqueue(CodeReviewExecutionLeaseOutcome.NoSlotAvailable);
        fixture.ExecutionLeases.Outcomes.Enqueue(CodeReviewExecutionLeaseOutcome.Acquired);
        var review = CodeReviewRecord();
        fixture.CodeReviews.Records.Add(review);
        fixture.ActiveLocks.Locks.Add(ActiveLockFor(review));

        var handleTask = fixture.ReviewRunHandler.HandleAsync(
            ReviewRunMessage(review),
            CancellationToken.None
        );
        await fixture.ExecutionLeases.WaitForAttemptsAsync(1);

        fixture.TimeProvider.Advance(TimeSpan.FromSeconds(30));
        await handleTask;

        await Assert.That(review.Status).IsEqualTo(CodeReviewStatus.Completed);
        await Assert.That(fixture.ActiveLocks.Locks).IsEmpty();
        await Assert.That(fixture.DelayedPublished).IsEmpty();
        await Assert
            .That(fixture.Published.OfType<GitHubCommentWriteRequested>().Single().Kind)
            .IsEqualTo("review_completed");
        await fixture
            .Runner.Received(1)
            .RunAsync(
                Arg.Any<CodeReviewRunRequested>(),
                Arg.Any<CodeReviewRecord>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ReviewRunHandler_WhenReviewAlreadyHasLiveLease_AcksWithoutStateChanges()
    {
        using var fixture = WorkflowFixture.Create();
        fixture.ExecutionLeases.NextOutcome =
            CodeReviewExecutionLeaseOutcome.AlreadyLeasedForThisReview;
        var review = CodeReviewRecord();
        fixture.CodeReviews.Records.Add(review);
        fixture.ActiveLocks.Locks.Add(ActiveLockFor(review));

        await fixture.ReviewRunHandler.HandleAsync(
            ReviewRunMessage(review),
            CancellationToken.None
        );

        await Assert.That(review.Status).IsEqualTo(CodeReviewStatus.Pending);
        await Assert.That(fixture.ActiveLocks.Locks).Count().IsEqualTo(1);
        await Assert.That(fixture.Published).IsEmpty();
        await Assert.That(fixture.DelayedPublished).IsEmpty();
        await fixture
            .Runner.Received(0)
            .RunAsync(
                Arg.Any<CodeReviewRunRequested>(),
                Arg.Any<CodeReviewRecord>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task ReviewRunHandler_WhenCapacityDeferralExpires_FailsReviewAndReleasesActiveLock()
    {
        using var fixture = WorkflowFixture.Create();
        fixture.ExecutionLeases.NextOutcome = CodeReviewExecutionLeaseOutcome.NoSlotAvailable;
        var review = CodeReviewRecord(createdAtUtc: DateTimeOffset.UtcNow.AddHours(-1));
        fixture.CodeReviews.Records.Add(review);
        fixture.ActiveLocks.Locks.Add(ActiveLockFor(review));

        await fixture.ReviewRunHandler.HandleAsync(
            ReviewRunMessage(review),
            CancellationToken.None
        );

        await Assert.That(review.Status).IsEqualTo(CodeReviewStatus.Errored);
        await Assert.That(review.FailureMessage).IsNotNull();
        await Assert.That(fixture.ActiveLocks.Locks).IsEmpty();
        await Assert.That(fixture.DelayedPublished).IsEmpty();
        await Assert
            .That(fixture.Published.OfType<GitHubCommentWriteRequested>().Single().Kind)
            .IsEqualTo("review_failed");
    }

    [Test]
    public async Task ReviewRunHandler_WithStaleTerminalRedelivery_DoesNotReleaseNewerActiveLock()
    {
        using var fixture = WorkflowFixture.Create();
        var oldReview = CodeReviewRecord();
        oldReview.Status = CodeReviewStatus.Completed;
        var newerReview = CodeReviewRecord(
            createdAtUtc: oldReview.CreatedAtUtc.AddMinutes(1),
            id: "cr_newer"
        );
        fixture.CodeReviews.Records.Add(oldReview);
        fixture.CodeReviews.Records.Add(newerReview);
        fixture.ActiveLocks.Locks.Add(ActiveLockFor(newerReview));

        await fixture.ReviewRunHandler.HandleAsync(
            ReviewRunMessage(oldReview),
            CancellationToken.None
        );

        var activeLock = fixture.ActiveLocks.Locks.Single();

        await Assert.That(activeLock.CodeReviewRecordId).IsEqualTo(newerReview.Id);
        await Assert.That(activeLock.CodeReviewCreatedAtUtc).IsEqualTo(newerReview.CreatedAtUtc);
        await Assert.That(fixture.Published).IsEmpty();
        await Assert.That(fixture.DelayedPublished).IsEmpty();
        await fixture
            .Runner.Received(0)
            .RunAsync(
                Arg.Any<CodeReviewRunRequested>(),
                Arg.Any<CodeReviewRecord>(),
                Arg.Any<CancellationToken>()
            );
    }

    private static GitHubPullRequestWebhookReceived PullRequestMessage(
        string action = "opened",
        bool isDraft = false,
        string? state = "open",
        string deliveryId = "delivery_123"
    ) =>
        new()
        {
            OrganizationId = "org_123",
            TeamId = "team_123",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            GitHubDeliveryId = deliveryId,
            GitHubEvent = "pull_request",
            GitHubAction = action,
            GitHubInstallationId = 141782517,
            TraceContext = EmptyTraceContext(),
            PullRequestNumber = 42,
            PullRequestNodeId = "PR_kwDOL123",
            Title = "Add review workflow",
            HtmlUrl = "https://github.com/zeeq-ai/zeeq/pull/42",
            IsDraft = isDraft,
            State = state,
            HeadRef = "feature/review-workflow",
            BaseRef = "main",
            HeadSha = "abc123",
            AuthorLogin = "octocat",
        };

    private static CodeReviewRecord CodeReviewRecord(
        int remainingReviewBudget = 0,
        DateTimeOffset? createdAtUtc = null,
        string id = "cr_test"
    )
    {
        var now = createdAtUtc ?? DateTimeOffset.UtcNow;

        return new()
        {
            Id = id,
            OrganizationId = "org_123",
            TeamId = "team_123",
            PullRequestRecordId = "pr_test",
            RepositoryId = "repo_123",
            OwnerQualifiedRepoName = "zeeq-ai/zeeq",
            PullRequestNumber = 42,
            Branch = "feature/review-workflow",
            Title = "Add review workflow",
            AuthorLogin = "octocat",
            Status = CodeReviewStatus.Pending,
            RequestOrigin = CodeReviewRequestOrigin.RepositoryWebhook,
            RemainingReviewBudget = remainingReviewBudget,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    private static CodeReviewRunRequested ReviewRunMessage(CodeReviewRecord review) =>
        new()
        {
            OrganizationId = review.OrganizationId,
            TeamId = review.TeamId,
            RepositoryId = review.RepositoryId!,
            OwnerQualifiedRepoName = review.OwnerQualifiedRepoName,
            PullRequestNumber = review.PullRequestNumber,
            PullRequestRecordId = review.PullRequestRecordId!,
            PullRequestCreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            CodeReviewRecordId = review.Id,
            CodeReviewCreatedAtUtc = review.CreatedAtUtc,
            GitHubDeliveryId = "delivery_123",
            TraceContext = EmptyTraceContext(),
        };

    private static ActiveCodeReviewLock ActiveLockFor(CodeReviewRecord review) =>
        new()
        {
            OrganizationId = review.OrganizationId,
            TeamId = review.TeamId,
            RepositoryId = review.RepositoryId!,
            PullRequestRecordId = review.PullRequestRecordId!,
            PullRequestCreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
            CodeReviewRecordId = review.Id,
            CodeReviewCreatedAtUtc = review.CreatedAtUtc,
            Status = CodeReviewStatus.Pending,
            AcquiredAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static ZeeqTraceContext EmptyTraceContext() => new(null, null);

    private static GitHubCommentTargetSelector PullRequestSummaryTarget() =>
        new(
            OrganizationId: "org_123",
            RepositoryId: "repo_123",
            PullRequestNumber: 42,
            Kind: GitHubCommentTargetKind.PullRequestSummary,
            ScopeKey: GitHubCommentMarkers.PullRequestSummaryScopeKey
        );

    private sealed class WorkflowFixture : IDisposable
    {
        private WorkflowFixture() { }

        private ServiceProvider? _serviceProvider;

        public AppSettings Settings { get; } =
            new()
            {
                Http = new HttpSettings { FrontendBaseUri = "http://zeeq-web.test" },
                CodeReview = new CodeReviewSettings
                {
                    ReviewRequestLinkEncryptionKey = "test-review-request-key",
                    ReviewRequestLinkValidityDays = 7,
                    DefaultReviewBudget = 10,
                },
            };

        public TestGitHubWebhookDeliveryStore Deliveries { get; } = new();
        public TestCodeRepositoryStore Repositories { get; } = new();
        public TestPullRequestLookupStore Lookups { get; } = new();
        public TestPullRequestRecordStore PullRequests { get; } = new();
        public TestCodeReviewRecordStore CodeReviews { get; } = new();
        public TestActiveCodeReviewLockStore ActiveLocks { get; } = new();
        public TestCodeReviewOrganizationSettingsStore OrganizationSettings { get; } = new();
        public TestCodeReviewExecutionLeaseStore ExecutionLeases { get; } = new();
        public IZeeqMessagePublisher Publisher { get; } = Substitute.For<IZeeqMessagePublisher>();
        public List<IRequest> Published { get; } = [];
        public List<(IRequest Message, TimeSpan Delay)> DelayedPublished { get; } = [];
        public FakeTimeProvider TimeProvider { get; } = new(DateTimeOffset.UtcNow);
        public ICodeReviewRunner Runner { get; private set; } = null!;
        public TestCodeReviewRuntimeStatistics RuntimeStatistics { get; } = new();
        public IAgentTelemetryPullRequestLinker TelemetryLinker { get; } =
            Substitute.For<IAgentTelemetryPullRequestLinker>();
        public IServiceScopeFactory ScopeFactory { get; private set; } = null!;

        public GitHubPullRequestWebhookReceivedHandler PullRequestHandler { get; private set; } =
            null!;
        public CodeReviewRunRequestedHandler ReviewRunHandler { get; private set; } = null!;

        public static WorkflowFixture Create(ICodeReviewRunner? runner = null)
        {
            var fixture = new WorkflowFixture();
            var deadLetters = Substitute.For<IDeadLetterWriter>();
            var settingsOptions = Options.Create(fixture.Settings);

            if (runner is null)
            {
                runner = Substitute.For<ICodeReviewRunner>();
                runner
                    .RunAsync(
                        Arg.Any<CodeReviewRunRequested>(),
                        Arg.Any<CodeReviewRecord>(),
                        Arg.Any<CancellationToken>()
                    )
                    .Returns(
                        Task.FromResult(
                            new CodeReviewRunResult(
                                string.Empty,
                                "postgres://code-review-findings/org_123/review_stub/test.xml",
                                0,
                                0,
                                0,
                                0,
                                0
                            )
                        )
                    );
            }

            fixture.Runner = runner;
            fixture
                .TelemetryLinker.LinkAsync(
                    Arg.Any<PullRequestRecord>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult(0));

            // Keep substitutes for collaborators whose behavior is only
            // call-capture or no-op. The stores below remain full in-memory
            // fakes because these workflow tests read their accumulated state;
            // encoding that state with mock setup would be harder to follow.
            CapturePublished<GitHubCommentWriteRequested>(fixture.Publisher, fixture.Published);
            CapturePublished<CodeReviewRunRequested>(fixture.Publisher, fixture.Published);
            CaptureDelayed<CodeReviewRunRequested>(fixture.Publisher, fixture.DelayedPublished);
            fixture._serviceProvider = new ServiceCollection()
                .AddSingleton<IActiveCodeReviewLockStore>(fixture.ActiveLocks)
                .BuildServiceProvider();
            fixture.ScopeFactory =
                fixture._serviceProvider.GetRequiredService<IServiceScopeFactory>();

            var reviewRequests = new CodeReviewRequestService(
                fixture.Lookups,
                fixture.PullRequests,
                fixture.CodeReviews,
                fixture.ActiveLocks,
                fixture.Publisher,
                settingsOptions,
                fixture.Repositories,
                Substitute.For<ICheckRunService>(),
                NullLogger<CodeReviewRequestService>.Instance
            );

            fixture.PullRequestHandler = new GitHubPullRequestWebhookReceivedHandler(
                deadLetters,
                fixture.Deliveries,
                fixture.Repositories,
                reviewRequests,
                fixture.TelemetryLinker,
                NullLogger<GitHubPullRequestWebhookReceivedHandler>.Instance
            );
            fixture.ReviewRunHandler = new CodeReviewRunRequestedHandler(
                deadLetters,
                fixture.CodeReviews,
                fixture.ActiveLocks,
                fixture.OrganizationSettings,
                fixture.ExecutionLeases,
                fixture.Publisher,
                runner,
                fixture.RuntimeStatistics,
                fixture.ScopeFactory,
                Substitute.For<ICheckRunService>(),
                fixture.PullRequests,
                fixture.Repositories,
                Substitute.For<IGitHubCommentClientFactory>(),
                new CodeReviewRequestLinkFactory(
                    settingsOptions,
                    new CodeReviewRequestTokenProtector(fixture.Settings.CodeReview)
                ),
                NullLogger<CodeReviewRunRequestedHandler>.Instance,
                fixture.TimeProvider
            );

            return fixture;
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
        }

        private static void CapturePublished<TMessage>(
            IZeeqMessagePublisher publisher,
            List<IRequest> published
        )
            where TMessage : class, IRequest
        {
            publisher
                .PublishAsync(
                    Arg.Do<TMessage>(message => published.Add(message)),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.CompletedTask);
        }

        private static void CaptureDelayed<TMessage>(
            IZeeqMessagePublisher publisher,
            List<(IRequest Message, TimeSpan Delay)> published
        )
            where TMessage : class, IRequest
        {
#pragma warning disable CS0618 // Intentionally watches the legacy delayed-publish API.
            publisher
                .PublishAfterAsync(
                    Arg.Any<TMessage>(),
                    Arg.Any<TimeSpan>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                {
                    published.Add(((TMessage)call[0], (TimeSpan)call[1]));

                    return Task.CompletedTask;
                });
#pragma warning restore CS0618
        }
    }

    private sealed class TestCodeRepositoryStore : ICodeRepositoryStore
    {
        public CodeRepository? ActiveRepository { get; set; } =
            new()
            {
                Id = "repo_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                Provider = "github",
                OwnerQualifiedName = "zeeq-ai/zeeq",
                DisplayName = "zeeq-ai/zeeq",
                Enabled = true,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

        public Task<CodeRepository?> FindActiveAsync(
            string provider,
            string ownerQualifiedName,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                ActiveRepository
                    is {
                        Enabled: true,
                        Provider: var activeProvider,
                        OwnerQualifiedName: var activeOwnerQualifiedName,
                    }
                && activeProvider == provider
                && activeOwnerQualifiedName == ownerQualifiedName
                    ? ActiveRepository
                    : null
            );

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeRepository>>(
                ActiveRepository is { OrganizationId: var activeOrganizationId }
                && activeOrganizationId == organizationId
                    ? [ActiveRepository]
                    : []
            );

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<CodeRepository>>(
                ActiveRepository is { OrganizationId: var activeOrganizationId }
                && activeOrganizationId == organizationId
                    ? [ActiveRepository]
                    : []
            );

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                ActiveRepository
                    is { OrganizationId: var activeOrganizationId, Id: var activeRepositoryId }
                && activeOrganizationId == organizationId
                && activeRepositoryId == repositoryId
                    ? ActiveRepository
                    : null
            );

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        )
        {
            ActiveRepository = repository;
            return Task.FromResult(repository);
        }

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => Task.FromResult(false);
    }

    private sealed class TestGitHubWebhookDeliveryStore : IGitHubWebhookDeliveryStore
    {
        public WebhookDeliveryClaimResult NextClaimResult { get; set; } =
            WebhookDeliveryClaimResult.Claimed;

        public List<GitHubWebhookDelivery> Claims { get; } = [];
        public List<string> ProcessedDeliveryIds { get; } = [];

        public Task<WebhookDeliveryClaimResult> ClaimAsync(
            GitHubWebhookDelivery delivery,
            CancellationToken cancellationToken
        )
        {
            Claims.Add(delivery);
            return Task.FromResult(NextClaimResult);
        }

        public Task MarkProcessedAsync(string deliveryId, CancellationToken cancellationToken)
        {
            ProcessedDeliveryIds.Add(deliveryId);
            return Task.CompletedTask;
        }
    }

    private sealed class TestPullRequestLookupStore : IPullRequestLookupStore
    {
        private readonly Dictionary<string, PullRequestLookup> _lookups = [];

        public Task<PullRequestLookup?> FindAsync(
            string organizationId,
            string repositoryId,
            int pullRequestNumber,
            CancellationToken cancellationToken
        )
        {
            _lookups.TryGetValue(
                Key(organizationId, repositoryId, pullRequestNumber),
                out var lookup
            );
            return Task.FromResult(lookup);
        }

        public Task<PullRequestLookup> UpsertAsync(
            PullRequestLookup lookup,
            CancellationToken cancellationToken
        )
        {
            _lookups[Key(lookup.OrganizationId, lookup.RepositoryId, lookup.PullRequestNumber)] =
                lookup;
            return Task.FromResult(lookup);
        }

        private static string Key(
            string organizationId,
            string repositoryId,
            int pullRequestNumber
        ) => $"{organizationId}:{repositoryId}:{pullRequestNumber}";
    }

    private sealed class TestPullRequestRecordStore : IPullRequestRecordStore
    {
        public List<PullRequestRecord> Records { get; } = [];

        public Task<PullRequestRecord> UpsertAsync(
            PullRequestRecord pullRequest,
            CancellationToken cancellationToken
        )
        {
            Records.RemoveAll(record => record.Id == pullRequest.Id);
            Records.Add(pullRequest);
            return Task.FromResult(pullRequest);
        }

        public Task<PullRequestRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Records.FirstOrDefault(record =>
                    record.Id == id && record.CreatedAtUtc == createdAtUtc
                )
            );

        public Task<PullRequestRecord?> FindByHeadShaAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Records.FirstOrDefault(record =>
                    record.OrganizationId == organizationId
                    && record.RepositoryId == repositoryId
                    && record.HeadSha == headSha
                )
            );

        public Task<PullRequestRecord?> FindByHeadShaWithCheckRunAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Records.FirstOrDefault(record =>
                    record.OrganizationId == organizationId
                    && record.RepositoryId == repositoryId
                    && record.HeadSha == headSha
                    && record.CheckRunState != null
                )
            );

        public Task<IReadOnlyList<PullRequestRecord>> FindByNumberAsync(
            string organizationId,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<PullRequestRecord>>(
                Records
                    .Where(record =>
                        record.OrganizationId == organizationId
                        && record.PullRequestNumber == pullRequestNumber
                    )
                    .ToList()
            );

        public Task<CodeReviewStreamPage<PullRequestRecord>> ListRecentAsync(
            PullRequestStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Paging is not used by workflow handler tests.");
    }

    private sealed class TestCodeReviewRecordStore : ICodeReviewRecordStore
    {
        public List<CodeReviewRecord> Records { get; } = [];

        public Task<CodeReviewRecord> AddAsync(
            CodeReviewRecord review,
            CancellationToken cancellationToken
        )
        {
            Records.Add(review);
            return Task.FromResult(review);
        }

        public Task<CodeReviewRecord> UpdateAsync(
            CodeReviewRecord review,
            CancellationToken cancellationToken
        ) => Task.FromResult(review);

        public Task<CodeReviewRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Records.FirstOrDefault(record =>
                    record.Id == id && record.CreatedAtUtc == createdAtUtc
                )
            );

        public Task<CodeReviewRecord?> FindNewestForPullRequestAsync(
            string organizationId,
            string pullRequestRecordId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Records
                    .Where(record =>
                        record.OrganizationId == organizationId
                        && record.PullRequestRecordId == pullRequestRecordId
                    )
                    .OrderByDescending(record => record.CreatedAtUtc)
                    .ThenByDescending(record => record.Id)
                    .FirstOrDefault()
            );

        public Task<CodeReviewStreamPage<CodeReviewRecord>> ListRecentAsync(
            CodeReviewStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Paging is not used by workflow handler tests.");

        public Task<CodeReviewStreamPage<CodeReviewRecord>> ListForPullRequestAsync(
            PullRequestReviewStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Paging is not used by workflow handler tests.");

        public Task<CodeReviewUpdateStreamPage> ListInboxUpdatesAsync(
            CodeReviewUpdateStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Paging is not used by workflow handler tests.");

        public Task<IReadOnlyList<CodeReviewRecord>> ListForAgentAsync(
            string organizationId,
            string? agentSessionId,
            string? reviewGroupId,
            int maxRecords,
            CancellationToken cancellationToken
        ) => throw new NotImplementedException();
    }

    private sealed class TestActiveCodeReviewLockStore : IActiveCodeReviewLockStore
    {
        public bool AllowAcquire { get; set; } = true;
        public List<ActiveCodeReviewLock> Locks { get; } = [];

        public Task<bool> TryAcquireAsync(
            ActiveCodeReviewLock activeLock,
            CancellationToken cancellationToken
        )
        {
            if (!AllowAcquire)
            {
                return Task.FromResult(false);
            }

            Locks.Add(activeLock);
            return Task.FromResult(true);
        }

        public Task<ActiveCodeReviewLock?> FindAsync(
            string organizationId,
            string pullRequestRecordId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Locks.FirstOrDefault(activeLock =>
                    activeLock.OrganizationId == organizationId
                    && activeLock.PullRequestRecordId == pullRequestRecordId
                )
            );

        public Task<bool> RefreshAsync(
            string organizationId,
            string pullRequestRecordId,
            TimeSpan ttl,
            CancellationToken cancellationToken
        )
        {
            var activeLock = Locks.FirstOrDefault(activeLock =>
                activeLock.OrganizationId == organizationId
                && activeLock.PullRequestRecordId == pullRequestRecordId
            );

            if (activeLock is null)
            {
                return Task.FromResult(false);
            }

            var now = DateTimeOffset.UtcNow;
            activeLock.ExpiresAtUtc = now.Add(ttl);
            activeLock.UpdatedAtUtc = now;

            return Task.FromResult(true);
        }

        public Task ReleaseAsync(
            string organizationId,
            string pullRequestRecordId,
            CancellationToken cancellationToken
        )
        {
            Locks.RemoveAll(activeLock =>
                activeLock.OrganizationId == organizationId
                && activeLock.PullRequestRecordId == pullRequestRecordId
            );
            return Task.CompletedTask;
        }

        public Task ReleaseIfOwnedByReviewAsync(
            string organizationId,
            string pullRequestRecordId,
            string codeReviewRecordId,
            DateTimeOffset codeReviewCreatedAtUtc,
            CancellationToken cancellationToken
        )
        {
            Locks.RemoveAll(activeLock =>
                activeLock.OrganizationId == organizationId
                && activeLock.PullRequestRecordId == pullRequestRecordId
                && activeLock.CodeReviewRecordId == codeReviewRecordId
                && activeLock.CodeReviewCreatedAtUtc == codeReviewCreatedAtUtc
            );
            return Task.CompletedTask;
        }
    }

    private sealed class TestCodeReviewOrganizationSettingsStore
        : ICodeReviewOrganizationSettingsStore
    {
        public CodeReviewOrganizationSettings Settings { get; } =
            new() { MaxConcurrentReviews = 2, ExecutionLeaseDuration = TimeSpan.FromMinutes(2) };

        public Task<CodeReviewOrganizationSettings> GetAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => Task.FromResult(Settings);

        public Task<CodeReviewOrganizationSettings> SaveAsync(
            string organizationId,
            CodeReviewOrganizationSettings settings,
            CancellationToken cancellationToken
        ) => Task.FromResult(settings);
    }

    private sealed class TestCodeReviewExecutionLeaseStore : ICodeReviewExecutionLeaseStore
    {
        public CodeReviewExecutionLeaseOutcome NextOutcome { get; set; } =
            CodeReviewExecutionLeaseOutcome.Acquired;
        public Queue<CodeReviewExecutionLeaseOutcome> Outcomes { get; } = [];
        public List<CodeReviewExecutionLease> Leases { get; } = [];
        public int AcquireAttempts { get; private set; }
        private readonly List<TaskCompletionSource> _attemptWaiters = [];

        public Task<CodeReviewExecutionLeaseResult> TryAcquireAsync(
            CodeReviewExecutionLeaseRequest request,
            CancellationToken cancellationToken
        )
        {
            AcquireAttempts++;
            NotifyAttemptWaiters();

            var outcome = Outcomes.TryDequeue(out var queuedOutcome) ? queuedOutcome : NextOutcome;

            if (outcome == CodeReviewExecutionLeaseOutcome.NoSlotAvailable)
            {
                return Task.FromResult(
                    new CodeReviewExecutionLeaseResult(
                        CodeReviewExecutionLeaseOutcome.NoSlotAvailable,
                        null
                    )
                );
            }

            var lease = CreateLease(request);
            if (outcome == CodeReviewExecutionLeaseOutcome.Acquired)
            {
                Leases.Add(lease);
            }

            return Task.FromResult(new CodeReviewExecutionLeaseResult(outcome, lease));
        }

        public Task WaitForAttemptsAsync(int expectedAttempts)
        {
            if (AcquireAttempts >= expectedAttempts)
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _attemptWaiters.Add(waiter);

            return waiter.Task;
        }

        private void NotifyAttemptWaiters()
        {
            foreach (var waiter in _attemptWaiters)
            {
                waiter.TrySetResult();
            }

            _attemptWaiters.Clear();
        }

        public Task<bool> RenewAsync(
            string leaseId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken
        )
        {
            var lease = Leases.FirstOrDefault(row => row.LeaseId == leaseId);
            if (lease is null)
            {
                return Task.FromResult(false);
            }

            lease.RenewedAtUtc = DateTimeOffset.UtcNow;
            lease.ExpiresAtUtc = lease.RenewedAtUtc.Add(leaseDuration);

            return Task.FromResult(true);
        }

        public Task ReleaseAsync(string leaseId, CancellationToken cancellationToken)
        {
            Leases.RemoveAll(lease => lease.LeaseId == leaseId);

            return Task.CompletedTask;
        }

        private static CodeReviewExecutionLease CreateLease(CodeReviewExecutionLeaseRequest request)
        {
            var now = DateTimeOffset.UtcNow;

            return new()
            {
                Id = "crl_test",
                OrganizationId = request.OrganizationId,
                TeamId = request.TeamId,
                SlotIndex = 0,
                LeaseId = "lease_test",
                RepositoryId = request.RepositoryId,
                PullRequestRecordId = request.PullRequestRecordId,
                PullRequestCreatedAtUtc = request.PullRequestCreatedAtUtc,
                CodeReviewRecordId = request.CodeReviewRecordId,
                CodeReviewCreatedAtUtc = request.CodeReviewCreatedAtUtc,
                AcquiredAtUtc = now,
                RenewedAtUtc = now,
                ExpiresAtUtc = now.Add(request.LeaseDuration),
                WorkerId = request.WorkerId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
        }
    }

    private sealed class ZeeqActivityListener : IDisposable
    {
        private readonly System.Diagnostics.ActivityListener _listener;

        public ZeeqActivityListener()
        {
            _listener = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = source => source.Name == ZeeqTelemetry.ActivitySourceName,
                Sample = (
                    ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _
                ) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref System.Diagnostics.ActivityCreationOptions<string> _) =>
                    System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => StoppedActivities.Enqueue(activity),
            };

            System.Diagnostics.ActivitySource.AddActivityListener(_listener);
        }

        public System.Collections.Concurrent.ConcurrentQueue<System.Diagnostics.Activity> StoppedActivities { get; } =
            new();

        public void Dispose() => _listener.Dispose();
    }

    private sealed class TestCodeReviewRuntimeStatistics : ICodeReviewRuntimeStatistics
    {
        public List<TimeSpan> Runtimes { get; } = [];

        public ValueTask RecordAsync(TimeSpan runtime, CancellationToken cancellationToken)
        {
            Runtimes.Add(runtime);

            return ValueTask.CompletedTask;
        }

        public CodeReviewRuntimePercentilesSnapshot GetSnapshot() =>
            Runtimes.Count == 0
                ? CodeReviewRuntimePercentilesSnapshot.NoData
                : new(
                    SampleCount: Runtimes.Count,
                    Percentile50: Runtimes[0],
                    Percentile95: Runtimes[^1]
                );
    }
}
