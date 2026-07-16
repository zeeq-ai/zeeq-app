using System.Security.Claims;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for the PR-number resolver endpoint (Mode 2 deep-link).
///
/// Run with:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/GetPullRequestByNumberHandlerTests/*"
/// </summary>
public sealed class GetPullRequestByNumberHandlerTests
{
    [Test]
    public async Task HandleAsync_WithValidRepoIdAndNumber_ReturnsOkWithPrDetail()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            organizationId: "org_123",
            repositoryId: "repo_123",
            pullRequestNumber: 42,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewPullRequestDetailResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.PullRequest.Id).IsEqualTo(fixture.PullRequest.Id);

        // Verify the repo-scoped resolver was called with the exact org/repo/number tuple —
        // this is the key contract that distinguishes this endpoint from the record-id lookup.
        await fixture
            .LookupStore.Received(1)
            .FindAsync("org_123", "repo_123", 42, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_WithLookupExistsButRecordMissing_ReturnsNotFound()
    {
        var fixture = Fixture.Create(recordMissing: true);

        var result = await fixture.Handler.HandleAsync(
            organizationId: "org_123",
            repositoryId: "repo_123",
            pullRequestNumber: 42,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_WithNoLookupRow_ReturnsNotFound()
    {
        var fixture = Fixture.Create(lookupMissing: true);

        var result = await fixture.Handler.HandleAsync(
            organizationId: "org_123",
            repositoryId: "repo_123",
            pullRequestNumber: 42,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_WithMissingRepositoryId_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            organizationId: "org_123",
            repositoryId: null,
            pullRequestNumber: 42,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_repository");
    }

    [Test]
    public async Task HandleAsync_WithMissingPullRequestNumber_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            organizationId: "org_123",
            repositoryId: "repo_123",
            pullRequestNumber: null,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_pull_request_number");
    }

    [Test]
    public async Task HandleAsync_WithZeroPullRequestNumber_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            organizationId: "org_123",
            repositoryId: "repo_123",
            pullRequestNumber: 0,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_pull_request_number");
    }

    [Test]
    public async Task HandleAsync_WithUnauthorizedOrg_ReturnsNotFound()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            organizationId: "org_456_not_member",
            repositoryId: "repo_123",
            pullRequestNumber: 42,
            TestUser(),
            CancellationToken.None
        );

        // Wrong org is indistinguishable from "no lookup row" — both return 404 (no cross-org leak).
        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    private static ClaimsPrincipal TestUser() =>
        new(
            new ClaimsIdentity(
                [
                    new Claim(OpenIddictConstants.Claims.Subject, "usr_123"),
                    new Claim(AuthClaims.OrganizationId, "org_123"),
                ],
                authenticationType: "test"
            )
        );

    private sealed class Fixture
    {
        private Fixture() { }

        public PullRequestRecord PullRequest { get; private init; } = null!;

        public GetPullRequestByNumberHandler Handler { get; private init; } = null!;

        public IPullRequestLookupStore LookupStore { get; private init; } = null!;

        public static Fixture Create(bool lookupMissing = false, bool recordMissing = false)
        {
            var pr = new PullRequestRecord
            {
                Id = "pr_123",
                OrganizationId = "org_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "owner/repo",
                PullRequestNumber = 42,
                GitHubNodeId = "PR_kwABC",
                Title = "Add feature X",
                Branch = "feature/x",
                BaseBranch = "main",
                HeadSha = "abc123",
                AuthorLogin = "octocat",
                HtmlUrl = "https://github.com/owner/repo/pull/42",
                IsDraft = false,
                State = PullRequestState.Open,
                ClaimStatus = PullRequestClaimStatus.Unclaimed,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                CreatedFromWebhookAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                LastWebhookAtUtc = DateTimeOffset.UtcNow,
            };

            var lookup = new PullRequestLookup
            {
                OrganizationId = "org_123",
                RepositoryId = "repo_123",
                OwnerQualifiedRepoName = "owner/repo",
                PullRequestNumber = 42,
                PullRequestRecordId = "pr_123",
                PullRequestCreatedAtUtc = pr.CreatedAtUtc,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

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

            var lookupStore = Substitute.For<IPullRequestLookupStore>();
            lookupStore
                .FindAsync("org_123", "repo_123", 42, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<PullRequestLookup?>(lookupMissing ? null : lookup));

            var pullRequestStore = Substitute.For<IPullRequestRecordStore>();
            pullRequestStore
                .FindAsync(
                    lookup.PullRequestRecordId,
                    lookup.PullRequestCreatedAtUtc,
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult<PullRequestRecord?>(recordMissing ? null : pr));

            return new Fixture
            {
                PullRequest = pr,
                LookupStore = lookupStore,
                Handler = new(
                    new CodeReviewAuthorization(memberships),
                    lookupStore,
                    pullRequestStore
                ),
            };
        }
    }
}
