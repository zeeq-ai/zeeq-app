using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using OpenIddict.Abstractions;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>Guards evaluation availability against stale client role and rollout assumptions.</summary>
public sealed class CodeReviewEvaluationAvailabilityTests
{
    [Test]
    [Arguments("owner", true)]
    [Arguments("admin", false)]
    public async Task Manager_ReceivesConfiguredAvailability(string role, bool enabled)
    {
        var handler = CreateHandler(role, enabled);

        var result = await handler.HandleAsync("org_alpha", User(), CancellationToken.None);

        var ok = result.Result as Ok<CodeReviewEvaluationAvailabilityResponse>;
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.Value!.Enabled).IsEqualTo(enabled);
        await Assert.That(ok.Value.MaxCases).IsEqualTo(25);
        await Assert.That(ok.Value.MaxConcurrentCases).IsEqualTo(2);
    }

    [Test]
    public async Task Member_CannotReadManagementAvailability()
    {
        var result = await CreateHandler("member", true)
            .HandleAsync("org_alpha", User(), CancellationToken.None);

        await Assert.That(result.Result is ForbidHttpResult).IsTrue();
    }

    [Test]
    public async Task AnotherOrganization_DoesNotRevealAvailability()
    {
        var result = await CreateHandler("owner", true)
            .HandleAsync("org_beta", User(), CancellationToken.None);

        await Assert.That(result.Result is NotFound).IsTrue();
    }

    private static GetCodeReviewEvaluationAvailabilityHandler CreateHandler(
        string role,
        bool enabled
    )
    {
        var memberships = Substitute.For<IZeeqMembershipStore>();
        memberships
            .ListActiveMembershipsForUserAsync("usr_alice", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<OrganizationMembership>>([
                    new()
                    {
                        Id = "mem_alice",
                        OrganizationId = "org_alpha",
                        UserId = "usr_alice",
                        Role = role,
                        Status = MembershipStatus.Active,
                        CreatedByUserId = "usr_alice",
                    },
                ])
            );

        return new(
            new CodeReviewAuthorization(memberships),
            new CodeReviewSettings { EvaluationsEnabled = enabled }
        );
    }

    private static ClaimsPrincipal User() =>
        new(
            new ClaimsIdentity([new Claim(OpenIddictConstants.Claims.Subject, "usr_alice")], "test")
        );
}
