using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

public sealed class TriageFindingHandlerTests
{
    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "user")], "test"));

    [Test]
    public async Task Save_PinsReviewToOrganizationAndUsesUiActor()
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
            .Returns([
                new OrganizationMembership
                {
                    Id = "mem",
                    OrganizationId = "org",
                    UserId = "user",
                    Role = "member",
                    Status = MembershipStatus.Active,
                    CreatedByUserId = "user",
                },
            ]);
        var reviews = Substitute.For<ICodeReviewRecordStore>();
        var time = DateTimeOffset.UtcNow;
        var review = new CodeReviewRecord
        {
            Id = "review",
            OrganizationId = "org",
            OwnerQualifiedRepoName = "owner/repo",
            Branch = "branch",
            Title = "title",
            AuthorLogin = "author",
            CreatedAtUtc = time,
        };
        reviews.FindAsync("review", time, Arg.Any<CancellationToken>()).Returns(review);
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var row = new CodeReviewFindingTriage
        {
            Id = "triage",
            OrganizationId = "org",
            OwnerQualifiedRepoName = "owner/repo",
            SourceReviewId = "review",
            SourceReviewCreatedAtUtc = time,
            OccurrenceKey = "F1",
            SemanticId = "some-valid-finding",
            Facet = "Security",
            FindingSummary = "Finding",
        };
        service
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new FindingTriageOutcome.Applied(row));
        var handler = new TriageFindingHandler(
            new(memberships),
            reviews,
            Substitute.For<ICodeReviewFindingTriageStore>(),
            service,
            null!
        );
        var result = await handler.SaveAsync(
            "org",
            "review",
            time,
            new("abcde.F1", CodeReviewFindingDisposition.FalsePositive, "reason"),
            User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is Ok<CodeReviewFindingTriageDto>).IsTrue();
        await service
            .Received(1)
            .TriageAsync(
                Arg.Is<FindingTriageRequest>(x =>
                    x.ActorUserId == "user"
                    && x.Channel == CodeReviewFindingTriageChannel.Ui
                    && ((FindingTriageTarget.ByReview)x.Target).CreatedAtUtc == time
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Test]
    public async Task Get_ExpiredReviewReturnsSnapshotAndNoLink()
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
            .Returns([
                new OrganizationMembership
                {
                    Id = "mem",
                    OrganizationId = "org",
                    UserId = "user",
                    Role = "member",
                    Status = MembershipStatus.Active,
                    CreatedByUserId = "user",
                },
            ]);
        var triages = Substitute.For<ICodeReviewFindingTriageStore>();
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
        triages.FindAsync("org", "triage", Arg.Any<CancellationToken>()).Returns(row);
        var handler = new TriageFindingHandler(
            new(memberships),
            Substitute.For<ICodeReviewRecordStore>(),
            triages,
            Substitute.For<ICodeReviewFindingTriageService>(),
            null!
        );
        var result = await handler.GetAsync("org", "triage", User(), CancellationToken.None);
        var ok = result.Result as Ok<FindingTriageResponse>;
        await Assert.That(ok!.Value!.SourceReviewExpired).IsTrue();
        await Assert.That(ok.Value.SingleReviewUrl).IsNull();
        await Assert.That(ok.Value.Triage.OccurrenceKey).IsEqualTo("F1");
    }

    [Test]
    public async Task Save_RequiresMembershipAndPartition()
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        var handler = new TriageFindingHandler(
            new(memberships),
            Substitute.For<ICodeReviewRecordStore>(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            service,
            null!
        );
        var request = new SaveFindingTriageRequest(
            "abcde.F1",
            CodeReviewFindingDisposition.FalsePositive,
            "reason"
        );
        var denied = await handler.SaveAsync(
            "org",
            "review",
            null,
            request,
            User(),
            CancellationToken.None
        );
        await Assert.That(denied.Result is NotFound).IsTrue();
        memberships
            .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
            .Returns([
                new OrganizationMembership
                {
                    Id = "mem",
                    OrganizationId = "org",
                    UserId = "user",
                    Role = "member",
                    Status = MembershipStatus.Active,
                    CreatedByUserId = "user",
                },
            ]);
        var missing = await handler.SaveAsync(
            "org",
            "review",
            null,
            request,
            User(),
            CancellationToken.None
        );
        await Assert.That(missing.Result is BadRequest<CodeReviewEndpointError>).IsTrue();
        await service
            .DidNotReceive()
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Confirm_PassesExpectedDispositionAndActor()
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("user", Arg.Any<CancellationToken>())
            .Returns([
                new OrganizationMembership
                {
                    Id = "mem",
                    OrganizationId = "org",
                    UserId = "user",
                    Role = "member",
                    Status = MembershipStatus.Active,
                    CreatedByUserId = "user",
                },
            ]);
        var service = Substitute.For<ICodeReviewFindingTriageService>();
        service
            .TriageAsync(Arg.Any<FindingTriageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new FindingTriageOutcome.Invalid(["mismatch"]));
        var handler = new TriageFindingHandler(
            new(memberships),
            Substitute.For<ICodeReviewRecordStore>(),
            Substitute.For<ICodeReviewFindingTriageStore>(),
            service,
            null!
        );
        var result = await handler.ConfirmAsync(
            "org",
            "triage",
            new(CodeReviewFindingDisposition.Deferred, "reason"),
            User(),
            CancellationToken.None
        );
        await Assert.That(result.Result is BadRequest<CodeReviewEndpointError>).IsTrue();
        await service
            .Received(1)
            .TriageAsync(
                Arg.Is<FindingTriageRequest>(x =>
                    x.ActorUserId == "user"
                    && x.ExpectedDisposition == CodeReviewFindingDisposition.Deferred
                    && x.Operation == FindingTriageOperation.Confirm
                ),
                Arg.Any<CancellationToken>()
            );
    }
}
