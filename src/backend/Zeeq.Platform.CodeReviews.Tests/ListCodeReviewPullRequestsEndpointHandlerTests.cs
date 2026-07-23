using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for the PR inbox list endpoint.
///
/// Run this test class:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/ListCodeReviewPullRequestsEndpointHandlerTests/*"
/// </summary>
public sealed class ListCodeReviewPullRequestsEndpointHandlerTests
{
    [Test]
    public async Task ListCodeReviewPullRequests_WithRows_ReturnsResumableReviewUpdateCursor()
    {
        var fixture = Fixture.Create();
        var beforeRequest = DateTimeOffset.UtcNow;

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            teamId: null,
            repositoryId: "repo_123",
            claimStatus: null,
            scope: null,
            cursorCreatedAtUtc: null,
            cursorId: null,
            pageSize: null,
            TestUser(),
            CancellationToken.None
        );

        var afterRequest = DateTimeOffset.UtcNow;
        var ok = result.Result as Ok<CodeReviewPullRequestListResponse>;
        var cursor = ok!.Value!.ReviewUpdatesCursor;
        var parsedCursor = CodeReviewEndpointMapping.ToUpdateCursor(
            cursor!.ReviewCreatedAtLowerBoundUtc,
            cursor.UpdatedAtUtc,
            cursor.CreatedAtUtc,
            cursor.Id,
            cursor.TeamId,
            cursor.RepositoryId,
            cursor.Scope,
            cursor.SubjectUserId
        );

        await Assert.That(ok).IsNotNull();
        await Assert.That(ok.Value.Items).HasSingleItem();
        await Assert.That(cursor).IsNotNull();
        await Assert.That(cursor.Id).IsEqualTo(CodeReviewUpdateCursor.SyntheticHighWaterId);
        await Assert.That(cursor.CreatedAtUtc).IsEqualTo(DateTimeOffset.MinValue);
        await Assert.That(cursor.UpdatedAtUtc).IsGreaterThanOrEqualTo(beforeRequest);
        await Assert.That(cursor.UpdatedAtUtc).IsLessThanOrEqualTo(afterRequest);
        await Assert
            .That(cursor.ReviewCreatedAtLowerBoundUtc)
            .IsEqualTo(fixture.PullRequest.CreatedAtUtc);
        await Assert.That(cursor.RepositoryId).IsEqualTo("repo_123");
        await Assert.That(parsedCursor).IsNotNull();
        await Assert.That(parsedCursor!.UpdatedAtUtc).IsEqualTo(cursor.UpdatedAtUtc);
    }

    [Test]
    public async Task ListCodeReviewPullRequests_WithMineScope_ReturnsSubjectScopedReviewUpdateCursor()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            teamId: null,
            repositoryId: "repo_123",
            claimStatus: null,
            scope: CodeReviewInboxScope.Mine,
            cursorCreatedAtUtc: null,
            cursorId: null,
            pageSize: null,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewPullRequestListResponse>;
        var cursor = ok!.Value!.ReviewUpdatesCursor;

        await Assert.That(cursor).IsNotNull();
        await Assert.That(cursor!.Scope).IsEqualTo(CodeReviewInboxScope.Mine);
        await Assert.That(cursor.SubjectUserId).IsEqualTo("usr_123");
        await Assert.That(fixture.PullRequests.LastQuery).IsNotNull();
        await Assert.That(fixture.PullRequests.LastQuery!.SubjectUserId).IsEqualTo("usr_123");
    }

    [Test]
    public async Task ListCodeReviewPullRequests_WithMineScopeAndMissingSubject_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            teamId: null,
            repositoryId: "repo_123",
            claimStatus: null,
            scope: CodeReviewInboxScope.Mine,
            cursorCreatedAtUtc: null,
            cursorId: null,
            pageSize: null,
            NoSubjectUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;

        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_subject");
        await Assert.That(fixture.PullRequests.LastQuery).IsNull();
    }

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [new Claim(OpenIddictConstants.Claims.Subject, "usr_123")],
                authenticationType: "test"
            )
        );

    private static ClaimsPrincipal NoSubjectUser() =>
        new(new ClaimsIdentity(authenticationType: "test"));

    private sealed class Fixture
    {
        private Fixture() { }

        public TestPullRequestRecordStore PullRequests { get; } = new();

        public PullRequestRecord PullRequest { get; } = CreatePullRequest();

        public ListCodeReviewPullRequestsHandler Handler { get; private set; } = null!;

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

            fixture.PullRequests.Page = new(
                [fixture.PullRequest],
                NextCursor: null,
                NewestCursor: new(fixture.PullRequest.CreatedAtUtc, fixture.PullRequest.Id)
            );
            fixture.Handler = new(new CodeReviewAuthorization(memberships), fixture.PullRequests);

            return fixture;
        }

        private static PullRequestRecord CreatePullRequest()
        {
            var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);

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
                LastWebhookAtUtc = createdAtUtc,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc,
            };
        }
    }

    private sealed class TestPullRequestRecordStore : IPullRequestRecordStore
    {
        public CodeReviewStreamPage<PullRequestRecord> Page { get; set; } = new([], null, null);

        public PullRequestStreamQuery? LastQuery { get; private set; }

        public Task<PullRequestRecord> UpsertAsync(
            PullRequestRecord pullRequest,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Writes are not used by these tests.");

        public Task<PullRequestRecord?> FindAsync(
            string id,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Lookup is not used by these tests.");

        public Task<PullRequestRecord?> FindByHeadShaAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Reverse-lookup is not used by these tests.");

        public Task<PullRequestRecord?> FindByHeadShaWithCheckRunAsync(
            string organizationId,
            string repositoryId,
            string headSha,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Check-run lookup is not used by these tests.");

        public Task<IReadOnlyList<PullRequestRecord>> FindByNumberAsync(
            string organizationId,
            int pullRequestNumber,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Number lookup is not used by these tests.");

        public Task<CodeReviewStreamPage<PullRequestRecord>> ListRecentAsync(
            PullRequestStreamQuery query,
            CancellationToken cancellationToken
        )
        {
            LastQuery = query;

            return Task.FromResult(Page);
        }
    }
}
