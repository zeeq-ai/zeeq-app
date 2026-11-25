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

    [Test]
    public async Task GetRun_ExposesFalsePositiveCountsAndDispositions()
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                    new()
                    {
                        Id = "member",
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
        var run = new CodeReviewEvaluationRun
        {
            Id = "run",
            OrganizationId = "org",
            RepositoryId = "repo",
            DatasetId = "dataset",
            DatasetVersion = 1,
            AgentId = "agent",
            ManifestJson = "{}",
            RequestedByUserId = "user",
            RequestKey = "key",
            State = CodeReviewEvaluationRunState.Incomplete,
            FalsePositiveCheckedCaseCount = 1,
            RepeatedFalsePositiveCount = 1,
            TrackedFalsePositiveCount = 2,
        };
        var reference = new CodeReviewEvaluationReferenceFinding(
            "r",
            "title",
            "summary",
            CodeReviewEvaluationFindingSeverity.Minor,
            "file.cs",
            null,
            CodeReviewEvaluationReferenceOrigin.Manual,
            null,
            null,
            "user",
            DateTimeOffset.UnixEpoch,
            CodeReviewEvaluationReferenceDisposition.Rejected,
            "reason"
        );
        var item = new CodeReviewEvaluationManifestCase(
            "case",
            903,
            "title",
            new("uri", "sha", 1),
            "base",
            "head",
            DateTimeOffset.UnixEpoch,
            1,
            0,
            new("agent", CodeReviewEvaluationReferenceState.Unlabelled, [reference])
        );
        var manifest = new CodeReviewEvaluationRunManifest(
            1,
            "org",
            "repo",
            "dataset",
            1,
            "agent",
            "reviewer",
            "facet",
            null,
            null!,
            null!,
            null!,
            "2",
            "",
            "",
            "{}",
            [],
            "build",
            "1",
            "user",
            "fingerprint",
            DateTimeOffset.UnixEpoch,
            null,
            [item]
        );
        var runs = Substitute.For<ICodeReviewEvaluationRunStore>();
        runs.FindAsync("org", "run", Arg.Any<CancellationToken>())
            .Returns(new CodeReviewEvaluationRunDetail(run, manifest));
        var results = Substitute.For<ICodeReviewEvaluationCaseResultStore>();
        results
            .ListForRunAsync("org", "run", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<CodeReviewEvaluationCaseResult>>([
                    new()
                    {
                        Id = "result",
                        OrganizationId = "org",
                        RunId = "run",
                        ManifestCaseId = "case",
                        PullRequestNumber = 903,
                        Phase = CodeReviewEvaluationCasePhase.Graded,
                        Outcome = CodeReviewEvaluationCaseOutcome.FalsePositiveChecked,
                        RepeatedFalsePositiveCount = 1,
                        TrackedFalsePositiveCount = 2,
                        JudgeMatchesJson =
                            """[{"referenceId":"r","candidateId":"c","reason":"same","disposition":"Rejected"}]""",
                    },
                ])
            );
        var handler = new EvaluationRunHandler(
            new(memberships),
            repositories,
            Substitute.For<ICodeReviewerAgentStore>(),
            Substitute.For<ICodeReviewerAgentRevisionStore>(),
            runs,
            results,
            Substitute.For<ICodeReviewEvaluationArtifactStore>(),
            null!,
            Substitute.For<IZeeqMessagePublisher>(),
            new CodeReviewSettings { EvaluationsEnabled = true }
        );
        var user = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "user")], "test")
        );
        var response =
            await handler.GetAsync("org", "repo", "run", user, CancellationToken.None)
            as Ok<EvaluationRunDetailDto>;
        await Assert.That(response).IsNotNull();
        var detail = response!.Value!;
        await Assert.That(detail.Run.Counts.FalsePositiveCheckedCaseCount).IsEqualTo(1);
        await Assert.That(detail.Run.Counts.RepeatedFalsePositiveCount).IsEqualTo(1);
        await Assert.That(detail.Run.Counts.TrackedFalsePositiveCount).IsEqualTo(2);
        var result = detail.Cases.Single();
        await Assert
            .That(result.Outcome)
            .IsEqualTo(CodeReviewEvaluationCaseOutcome.FalsePositiveChecked);
        await Assert.That(result.RepeatedFalsePositiveCount).IsEqualTo(1);
        await Assert.That(result.TrackedFalsePositiveCount).IsEqualTo(2);
        await Assert
            .That(result.References.Findings.Single().Disposition)
            .IsEqualTo(CodeReviewEvaluationReferenceDisposition.Rejected);
        await Assert
            .That(result.JudgeMatches.Single().Disposition)
            .IsEqualTo(CodeReviewEvaluationReferenceDisposition.Rejected);
    }
}
