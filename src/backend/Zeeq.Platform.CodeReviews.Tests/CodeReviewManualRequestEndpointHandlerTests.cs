using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenIddict.Abstractions;
using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for manual code-review requests.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewManualRequestEndpointHandlerTests/*"
/// </summary>
public sealed class CodeReviewManualRequestEndpointHandlerTests
{
    [Test]
    public async Task RequestCodeReview_WithDraftPullRequest_QueuesManualReview()
    {
        var fixture = Fixture.Create();
        fixture.PullRequest.IsDraft = true;
        fixture.PullRequest.ClaimStatus = PullRequestClaimStatus.Claimed;
        fixture.PullRequest.ClaimedByUserId = "usr_456";
        fixture.PullRequest.FeatureId = "feat_123";
        fixture.PullRequests.Records.Add(fixture.PullRequest);
        var originalLastWebhookAtUtc = fixture.PullRequest.LastWebhookAtUtc;

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.PullRequest.Id,
            fixture.PullRequest.CreatedAtUtc,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewManualRequestResponse>;
        var immediate = fixture.Publisher.Published.OfType<GitHubCommentWriteRequested>().Single();
        var run = fixture.Publisher.Published.OfType<CodeReviewRunRequested>().Single();
        var review = fixture.CodeReviews.Records.Single();
        var pullRequest = fixture.PullRequests.Records.Single();

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Outcome).IsEqualTo(CodeReviewRequestOutcome.Queued);
        await Assert
            .That(ok.Value.CodeReview!.RequestOrigin)
            .IsEqualTo(CodeReviewRequestOrigin.Manual);
        await Assert.That(review.RequestOrigin).IsEqualTo(CodeReviewRequestOrigin.Manual);
        await Assert.That(review.PullRequestRecordId).IsEqualTo(fixture.PullRequest.Id);
        await Assert.That(immediate.Kind).IsEqualTo("queued");
        await Assert.That(run.CodeReviewRecordId).IsEqualTo(review.Id);
        await Assert.That(pullRequest.IsDraft).IsTrue();
        await Assert.That(pullRequest.ClaimStatus).IsEqualTo(PullRequestClaimStatus.Claimed);
        await Assert.That(pullRequest.ClaimedByUserId).IsEqualTo("usr_456");
        await Assert.That(pullRequest.FeatureId).IsEqualTo("feat_123");
        await Assert.That(pullRequest.LastWebhookAtUtc).IsEqualTo(originalLastWebhookAtUtc);
    }

    [Test]
    public async Task RequestCodeReview_WithActiveReview_PublishesAlreadyRunningAcknowledgementOnly()
    {
        var fixture = Fixture.Create();
        fixture.ActiveLocks.AllowAcquire = false;
        fixture.PullRequests.Records.Add(fixture.PullRequest);

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.PullRequest.Id,
            fixture.PullRequest.CreatedAtUtc,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewManualRequestResponse>;
        var immediate = fixture.Publisher.Published.OfType<GitHubCommentWriteRequested>().Single();

        await Assert.That(ok).IsNotNull();
        await Assert
            .That(ok!.Value!.Outcome)
            .IsEqualTo(CodeReviewRequestOutcome.ActiveReviewAlreadyRunning);
        await Assert.That(immediate.Kind).IsEqualTo("already_running");
        await Assert.That(fixture.CodeReviews.Records).IsEmpty();
        await Assert.That(fixture.Publisher.Published.OfType<CodeReviewRunRequested>()).IsEmpty();
    }

    [Test]
    public async Task RequestCodeReview_WithoutCreatedAt_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.PullRequest.Id,
            createdAtUtc: null,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_created_at");
        await Assert.That(fixture.Publisher.Published).IsEmpty();
    }

    [Test]
    public async Task RequestCodeReview_WithDisabledRepository_ReturnsNotFound()
    {
        var fixture = Fixture.Create();
        fixture.Repositories.ActiveRepository = null;
        fixture.PullRequests.Records.Add(fixture.PullRequest);

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.PullRequest.Id,
            fixture.PullRequest.CreatedAtUtc,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result is NotFound).IsTrue();
        await Assert.That(fixture.CodeReviews.Records).IsEmpty();
        await Assert.That(fixture.Publisher.Published).IsEmpty();
    }

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, "usr_123")],
                authenticationType: "test"
            )
        );

    private sealed class Fixture
    {
        private Fixture() { }

        public TestPullRequestLookupStore Lookups { get; } = new();

        public TestPullRequestRecordStore PullRequests { get; } = new();

        public TestCodeReviewRecordStore CodeReviews { get; } = new();

        public TestActiveCodeReviewLockStore ActiveLocks { get; } = new();

        public TestCodeRepositoryStore Repositories { get; } = new();

        public TestPublisher Publisher { get; } = new();

        public PullRequestRecord PullRequest { get; } = CreatePullRequest();

        public RequestCodeReviewHandler Handler { get; private set; } = null!;

        public static Fixture Create()
        {
            var fixture = new Fixture();
            var memberships = Substitute.For<IZeeqMembershipStore>();
            memberships
                .ListActiveMembershipsForUserAsync("usr_123", Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                        new()
                        {
                            Id = "mem_123",
                            OrganizationId = "org_123",
                            UserId = "usr_123",
                            Role = "member",
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "usr_123",
                        },
                    ])
                );

            var settings = Options.Create(
                new AppSettings
                {
                    Http = new HttpSettings { FrontendBaseUri = "http://zeeq-web.test" },
                    CodeReview = new CodeReviewSettings
                    {
                        ReviewRequestLinkEncryptionKey = "test-review-request-key",
                        ReviewRequestLinkValidityDays = 7,
                        DefaultReviewBudget = 10,
                    },
                }
            );
            var reviewRequests = new CodeReviewRequestService(
                fixture.Lookups,
                fixture.PullRequests,
                fixture.CodeReviews,
                fixture.ActiveLocks,
                fixture.Publisher,
                settings,
                fixture.Repositories,
                Substitute.For<ICheckRunService>(),
                NullLogger<CodeReviewRequestService>.Instance
            );

            fixture.Handler = new RequestCodeReviewHandler(
                new CodeReviewAuthorization(memberships),
                fixture.PullRequests,
                fixture.Repositories,
                reviewRequests
            );

            return fixture;
        }

        private static PullRequestRecord CreatePullRequest()
        {
            var createdAtUtc = DateTimeOffset.UtcNow.AddDays(-1);
            var updatedAtUtc = createdAtUtc.AddHours(1);
            var lastWebhookAtUtc = createdAtUtc.AddMinutes(5);

            return new()
            {
                Id = "pr_123",
                OrganizationId = "org_123",
                TeamId = "team_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "zeeq-ai/zeeq",
                PullRequestNumber = 42,
                GitHubNodeId = "PR_kwDOL123",
                Title = "Add review workflow",
                Branch = "feature/review-workflow",
                BaseBranch = "main",
                HeadSha = "abc123",
                AuthorLogin = "octocat",
                HtmlUrl = "https://github.com/zeeq-ai/zeeq/pull/42",
                IsDraft = false,
                State = PullRequestState.Open,
                ClaimStatus = PullRequestClaimStatus.Unclaimed,
                CreatedFromWebhookAtUtc = createdAtUtc,
                LastWebhookAtUtc = lastWebhookAtUtc,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
            };
        }
    }

    private sealed class TestPublisher : IZeeqMessagePublisher
    {
        public List<IRequest> Published { get; } = [];

        public Task PublishAsync<TMessage>(
            TMessage message,
            CancellationToken cancellationToken = default
        )
            where TMessage : class, IRequest
        {
            Published.Add(message);

            return Task.CompletedTask;
        }

        public Task PublishAfterAsync<TMessage>(
            TMessage message,
            TimeSpan delay,
            CancellationToken cancellationToken = default
        )
            where TMessage : class, IRequest
        {
            Published.Add(message);

            return Task.CompletedTask;
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
        ) => Task.FromResult(ActiveRepository);

        public Task<IReadOnlyList<CodeRepository>> ListActiveForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Listing repositories is not used by these tests.");

        public Task<IReadOnlyList<CodeRepository>> ListConfiguredForOrganizationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Listing repositories is not used by these tests.");

        public Task<CodeRepository?> FindActiveForOrganizationAsync(
            string organizationId,
            string repositoryId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                ActiveRepository is { OrganizationId: var activeOrganizationId, Id: var activeId }
                && activeOrganizationId == organizationId
                && activeId == repositoryId
                    ? ActiveRepository
                    : null
            );

        public Task<CodeRepository> UpsertAsync(
            CodeRepository repository,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository writes are not used by these tests.");

        public Task<bool> DisableAsync(
            string organizationId,
            string repositoryId,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Repository writes are not used by these tests.");
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
        ) => throw new NotSupportedException("Paging is not used by these tests.");
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
        ) => throw new NotSupportedException("Review updates are not used by these tests.");

        public Task<CodeReviewRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Review lookup is not used by these tests.");

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
        ) => throw new NotSupportedException("Paging is not used by these tests.");

        public Task<CodeReviewStreamPage<CodeReviewRecord>> ListForPullRequestAsync(
            PullRequestReviewStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Paging is not used by these tests.");

        public Task<CodeReviewUpdateStreamPage> ListInboxUpdatesAsync(
            CodeReviewUpdateStreamQuery query,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Paging is not used by these tests.");

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
        ) => throw new NotSupportedException("Lock lookup is not used by these tests.");

        public Task<bool> RefreshAsync(
            string organizationId,
            string pullRequestRecordId,
            TimeSpan ttl,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Lock refresh is not used by these tests.");

        public Task ReleaseAsync(
            string organizationId,
            string pullRequestRecordId,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Lock release is not used by these tests.");

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
}
