using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class CodeReviewEvaluationRunEndpointTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequestKeyLongerThanDatabaseLimitIsRejectedBeforeAdmission(bool retry)
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                    new()
                    {
                        Id = "membership",
                        OrganizationId = "org",
                        UserId = "user",
                        Role = "owner",
                        Status = MembershipStatus.Active,
                        CreatedByUserId = "user",
                    },
                ])
            );
        var repositories = Substitute.For<ICodeRepositoryStore>();
        repositories
            .FindActiveForOrganizationAsync("org", "repo", Arg.Any<CancellationToken>())
            .Returns(
                new CodeRepository
                {
                    Id = "repo",
                    OrganizationId = "org",
                    Provider = "github",
                    OwnerQualifiedName = "owner/repo",
                    DisplayName = "repo",
                    Enabled = true,
                }
            );
        var runs = Substitute.For<ICodeReviewEvaluationRunStore>();
        runs.FindAsync("org", "parent", Arg.Any<CancellationToken>())
            .Returns(
                new CodeReviewEvaluationRunDetail(
                    new CodeReviewEvaluationRun
                    {
                        Id = "parent",
                        OrganizationId = "org",
                        RepositoryId = "repo",
                        DatasetId = "dataset",
                        DatasetVersion = 1,
                        AgentId = "agent",
                        ManifestJson = "{}",
                        RequestedByUserId = "user",
                        RequestKey = "original",
                        State = CodeReviewEvaluationRunState.Incomplete,
                    },
                    null!
                )
            );
        var publisher = Substitute.For<IZeeqMessagePublisher>();
        var handler = new EvaluationRunHandler(
            new(memberships),
            repositories,
            Substitute.For<ICodeReviewerAgentStore>(),
            Substitute.For<ICodeReviewerAgentRevisionStore>(),
            runs,
            Substitute.For<ICodeReviewEvaluationCaseResultStore>(),
            Substitute.For<ICodeReviewEvaluationArtifactStore>(),
            null!,
            publisher,
            new CodeReviewSettings { EvaluationsEnabled = true }
        );
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "user")], "test")
        );
        var key = new string('x', 129);

        var result = retry
            ? await handler.MutateAsync(
                "org",
                "repo",
                "parent",
                "retry",
                new(key),
                user,
                CancellationToken.None
            )
            : await handler.CreateAsync(
                "org",
                "repo",
                new("dataset", 1, "agent", null, null, key),
                user,
                CancellationToken.None
            );

        await Assert.That(result is BadRequest<CodeReviewEndpointError>).IsTrue();
        await runs.DidNotReceive()
            .AdmitAsync(Arg.Any<CodeReviewEvaluationRunAdmission>(), Arg.Any<CancellationToken>());
        await runs.DidNotReceive()
            .AdmitRetryAsync(
                Arg.Any<CodeReviewEvaluationRunRetryAdmission>(),
                Arg.Any<CancellationToken>()
            );
        await publisher
            .DidNotReceive()
            .PublishAsync(
                Arg.Any<CodeReviewEvaluationRunRequested>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    [Arguments("member", true)]
    [Arguments("owner", false)]
    public async Task UnauthorizedRunAdmissionDoesNotReadOrWriteRuns(
        string role,
        bool sameOrganization
    )
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                    new()
                    {
                        Id = "membership",
                        OrganizationId = sameOrganization ? "org" : "other",
                        UserId = "user",
                        Role = role,
                        Status = MembershipStatus.Active,
                        CreatedByUserId = "user",
                    },
                ])
            );
        var store = Substitute.For<ICodeReviewEvaluationRunStore>();
        var publisher = Substitute.For<IZeeqMessagePublisher>();
        var handler = new EvaluationRunHandler(
            new(memberships),
            Substitute.For<ICodeRepositoryStore>(),
            Substitute.For<ICodeReviewerAgentStore>(),
            Substitute.For<ICodeReviewerAgentRevisionStore>(),
            store,
            Substitute.For<ICodeReviewEvaluationCaseResultStore>(),
            Substitute.For<ICodeReviewEvaluationArtifactStore>(),
            null!,
            publisher,
            new CodeReviewSettings { EvaluationsEnabled = true }
        );
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "user")], "test")
        );
        var result = await handler.CreateAsync(
            "org",
            "repo",
            new("dataset", 1, "agent", null, null, "key"),
            user,
            CancellationToken.None
        );
        await Assert
            .That(sameOrganization ? result is ForbidHttpResult : result is NotFound)
            .IsTrue();
        await store
            .DidNotReceive()
            .AdmitAsync(Arg.Any<CodeReviewEvaluationRunAdmission>(), Arg.Any<CancellationToken>());
        await publisher
            .DidNotReceive()
            .PublishAsync(
                Arg.Any<CodeReviewEvaluationRunRequested>(),
                Arg.Any<CancellationToken>()
            );
    }
}
