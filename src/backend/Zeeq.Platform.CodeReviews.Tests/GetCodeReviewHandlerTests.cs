using System.Security.Claims;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Handler unit tests for the single-review deep-link endpoint.
///
/// Run with:
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/GetCodeReviewHandlerTests/*"
/// </summary>
public sealed class GetCodeReviewHandlerTests
{
    [Test]
    public async Task HandleAsync_WithoutCreatedAt_ReturnsBadRequest()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.PrReview.Id,
            createdAtUtc: null,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        var badRequest = result.Result as BadRequest<CodeReviewEndpointError>;
        await Assert.That(badRequest).IsNotNull();
        await Assert.That(badRequest!.Value!.Code).IsEqualTo("missing_created_at");
    }

    [Test]
    public async Task HandleAsync_WithNonMemberUser_ReturnsNotFound()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_456_not_member",
            fixture.PrReview.Id,
            fixture.PrReview.CreatedAtUtc,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_WithMissingReview_ReturnsNotFound()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            "cr_does_not_exist",
            DateTimeOffset.UtcNow,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_WithOrgMismatchOnRecord_ReturnsNotFound()
    {
        var fixture = Fixture.Create();

        // The review's OrganizationId is "org_123", but we request it under "org_other"
        // (which the user IS a member of).
        var result = await fixture.Handler.HandleAsync(
            "org_other",
            fixture.PrReview.Id,
            fixture.PrReview.CreatedAtUtc,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        await Assert.That(result.Result).IsTypeOf<NotFound>();
    }

    [Test]
    public async Task HandleAsync_WithAgentReview_DefaultsToAgentModeAndIncludesRelatedSet()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.AgentReview.Id,
            fixture.AgentReview.CreatedAtUtc,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewSingleViewResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Mode).IsEqualTo(CodeReviewSingleViewMode.Agent);
        // Primary is always first; related set is loaded via ListForAgentAsync.
        await Assert.That(ok.Value.Reviews[0].Id).IsEqualTo(fixture.AgentReview.Id);
        await Assert.That(ok.Value.Reviews).Count().IsEqualTo(2);
    }

    [Test]
    public async Task HandleAsync_WithPrReview_DefaultsToPrModeAndIncludesRelatedSet()
    {
        var fixture = Fixture.Create();

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.PrReview.Id,
            fixture.PrReview.CreatedAtUtc,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewSingleViewResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Mode).IsEqualTo(CodeReviewSingleViewMode.Pr);
        // Primary first, related set from ListForPullRequestAsync.
        await Assert.That(ok.Value.Reviews[0].Id).IsEqualTo(fixture.PrReview.Id);
        await Assert.That(ok.Value.Reviews).Count().IsEqualTo(2);
    }

    [Test]
    public async Task HandleAsync_WithModeOverride_UsesSpecifiedModeInsteadOfDefault()
    {
        var fixture = Fixture.Create();

        // Agent review forced into PR mode via explicit override.
        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.AgentReview.Id,
            fixture.AgentReview.CreatedAtUtc,
            mode: CodeReviewSingleViewMode.Pr,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewSingleViewResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Mode).IsEqualTo(CodeReviewSingleViewMode.Pr);
    }

    [Test]
    public async Task HandleAsync_WhenPrimaryNotInRelatedSet_InsertsItFirst()
    {
        var fixture = Fixture.Create(primaryAbsentFromRelatedSet: true);

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.AgentReview.Id,
            fixture.AgentReview.CreatedAtUtc,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewSingleViewResponse>;
        await Assert.That(ok).IsNotNull();
        // First item must be the primary even though ListForAgentAsync omitted it.
        await Assert.That(ok!.Value!.Reviews[0].Id).IsEqualTo(fixture.AgentReview.Id);
        // Primary added + the related set item = 2 total.
        await Assert.That(ok.Value.Reviews).Count().IsEqualTo(2);
    }

    [Test]
    public async Task HandleAsync_WhenPrimaryAppearsMultipleTimesInRelatedSet_ReturnsItOnceFirst()
    {
        var fixture = Fixture.Create(primaryDuplicatedInRelatedSet: true);

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.AgentReview.Id,
            fixture.AgentReview.CreatedAtUtc,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewSingleViewResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Reviews).Count().IsEqualTo(2);
        await Assert.That(ok.Value.Reviews[0].Id).IsEqualTo(fixture.AgentReview.Id);
        await Assert.That(ok.Value.Reviews[1].Id).IsEqualTo("cr_agent_related");
    }

    [Test]
    public async Task HandleAsync_WithPrModeAndMissingPullRequestLookup_ReturnsOnlyPrimary()
    {
        var fixture = Fixture.Create(missingPullRequestLookup: true);

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.PrReview.Id,
            fixture.PrReview.CreatedAtUtc,
            mode: CodeReviewSingleViewMode.Pr,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewSingleViewResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Reviews).Count().IsEqualTo(1);
        await Assert.That(ok.Value.Reviews[0].Id).IsEqualTo(fixture.PrReview.Id);
        await fixture
            .ReviewStore.DidNotReceive()
            .ListForPullRequestAsync(
                Arg.Any<PullRequestReviewStreamQuery>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task HandleAsync_WhenNeitherSessionNorGroupSet_ReturnsOnlyPrimaryForAgentMode()
    {
        var fixture = Fixture.Create(agentHasNoSessionOrGroup: true);

        var result = await fixture.Handler.HandleAsync(
            "org_123",
            fixture.AgentReview.Id,
            fixture.AgentReview.CreatedAtUtc,
            mode: null,
            TestUser(),
            CancellationToken.None
        );

        var ok = result.Result as Ok<CodeReviewSingleViewResponse>;
        await Assert.That(ok).IsNotNull();
        // ListForAgentAsync returns [] when both keys are null; primary is added by EnsurePrimaryFirst.
        await Assert.That(ok!.Value!.Reviews).Count().IsEqualTo(1);
        await Assert.That(ok.Value.Reviews[0].Id).IsEqualTo(fixture.AgentReview.Id);
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

        public CodeReviewRecord AgentReview { get; private init; } = null!;

        public CodeReviewRecord PrReview { get; private init; } = null!;

        public GetCodeReviewHandler Handler { get; private init; } = null!;

        public ICodeReviewRecordStore ReviewStore { get; private init; } = null!;

        public static Fixture Create(
            bool primaryAbsentFromRelatedSet = false,
            bool primaryDuplicatedInRelatedSet = false,
            bool missingPullRequestLookup = false,
            bool agentHasNoSessionOrGroup = false
        )
        {
            var agentReview = new CodeReviewRecord
            {
                Id = "cr_agent",
                OrganizationId = "org_123",
                PullRequestRecordId = null,
                RepositoryId = null,
                PullRequestNumber = 0,
                Branch = string.Empty,
                Title = "Agent review",
                AuthorLogin = string.Empty,
                OwnerQualifiedRepoName = string.Empty,
                AgentSessionId = agentHasNoSessionOrGroup ? null : "sess_abc",
                ReviewGroupId = agentHasNoSessionOrGroup ? null : "crg_abc",
                Status = CodeReviewStatus.Completed,
                RequestOrigin = CodeReviewRequestOrigin.Agent,
                RemainingReviewBudget = 10,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var prReview = new CodeReviewRecord
            {
                Id = "cr_pr",
                OrganizationId = "org_123",
                PullRequestRecordId = "pr_123",
                RepositoryId = "repo_123",
                PullRequestNumber = 7,
                Branch = "feature/test",
                Title = "PR review",
                AuthorLogin = "octocat",
                OwnerQualifiedRepoName = "owner/repo",
                Status = CodeReviewStatus.Completed,
                RequestOrigin = CodeReviewRequestOrigin.RepositoryWebhook,
                RemainingReviewBudget = 10,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var agentRelated = new CodeReviewRecord
            {
                Id = "cr_agent_related",
                OrganizationId = "org_123",
                PullRequestRecordId = null,
                RepositoryId = null,
                PullRequestNumber = 0,
                Branch = string.Empty,
                Title = "Earlier agent review",
                AuthorLogin = string.Empty,
                OwnerQualifiedRepoName = string.Empty,
                AgentSessionId = "sess_abc",
                ReviewGroupId = "crg_abc",
                Status = CodeReviewStatus.Completed,
                RequestOrigin = CodeReviewRequestOrigin.Agent,
                RemainingReviewBudget = 10,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-3),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var prRelated = new CodeReviewRecord
            {
                Id = "cr_pr_related",
                OrganizationId = "org_123",
                PullRequestRecordId = "pr_123",
                RepositoryId = "repo_123",
                PullRequestNumber = 7,
                Branch = "feature/test",
                Title = "Earlier PR review",
                AuthorLogin = "octocat",
                OwnerQualifiedRepoName = "owner/repo",
                Status = CodeReviewStatus.Completed,
                RequestOrigin = CodeReviewRequestOrigin.RepositoryWebhook,
                RemainingReviewBudget = 10,
                CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-4),
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
                        new()
                        {
                            Id = "mem_456",
                            OrganizationId = "org_other",
                            UserId = "usr_123",
                            Role = "member",
                            Status = MembershipStatus.Active,
                            CreatedByUserId = "usr_123",
                        },
                    ])
                );

            var agentRelatedSet = AgentRelatedSet(
                agentReview,
                agentRelated,
                primaryAbsentFromRelatedSet,
                primaryDuplicatedInRelatedSet,
                agentHasNoSessionOrGroup
            );

            var reviewStore = Substitute.For<ICodeReviewRecordStore>();
            reviewStore
                .FindAsync(agentReview.Id, agentReview.CreatedAtUtc, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<CodeReviewRecord?>(agentReview));
            reviewStore
                .FindAsync(prReview.Id, prReview.CreatedAtUtc, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<CodeReviewRecord?>(prReview));
            reviewStore
                .ListForAgentAsync(
                    "org_123",
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult(agentRelatedSet));
            reviewStore
                .ListForPullRequestAsync(
                    Arg.Any<PullRequestReviewStreamQuery>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(
                    Task.FromResult(
                        new CodeReviewStreamPage<CodeReviewRecord>(
                            Items: [prReview, prRelated],
                            NextCursor: null,
                            NewestCursor: null
                        )
                    )
                );

            var lookupStore = Substitute.For<IPullRequestLookupStore>();
            lookupStore
                .FindAsync("org_123", "repo_123", 7, Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<PullRequestLookup?>(
                        missingPullRequestLookup
                            ? null
                            : new PullRequestLookup
                            {
                                OrganizationId = "org_123",
                                RepositoryId = "repo_123",
                                OwnerQualifiedRepoName = "owner/repo",
                                PullRequestNumber = 7,
                                PullRequestRecordId = "pr_123",
                                PullRequestCreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                                UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                            }
                    )
                );

            var recordStore = Substitute.For<IPullRequestRecordStore>();
            recordStore
                .FindAsync(
                    Arg.Any<string>(),
                    Arg.Any<DateTimeOffset>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(Task.FromResult<PullRequestRecord?>(null));

            return new Fixture
            {
                AgentReview = agentReview,
                PrReview = prReview,
                ReviewStore = reviewStore,
                Handler = new(
                    new CodeReviewAuthorization(memberships),
                    reviewStore,
                    lookupStore,
                    recordStore
                ),
            };
        }

        private static IReadOnlyList<CodeReviewRecord> AgentRelatedSet(
            CodeReviewRecord agentReview,
            CodeReviewRecord agentRelated,
            bool primaryAbsentFromRelatedSet,
            bool primaryDuplicatedInRelatedSet,
            bool agentHasNoSessionOrGroup
        )
        {
            if (agentHasNoSessionOrGroup)
            {
                return [];
            }

            if (primaryAbsentFromRelatedSet)
            {
                return [agentRelated];
            }

            if (primaryDuplicatedInRelatedSet)
            {
                return [agentReview, agentReview, agentRelated];
            }

            return [agentReview, agentRelated];
        }
    }
}
