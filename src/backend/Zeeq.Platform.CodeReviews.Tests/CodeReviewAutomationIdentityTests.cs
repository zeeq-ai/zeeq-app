using System.Security.Claims;
using OpenIddict.Abstractions;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests for <see cref="CodeReviewAutomationIdentity"/>.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewAutomationIdentityTests/*"
/// </summary>
public sealed class CodeReviewAutomationIdentityTests
{
    [Test]
    public async Task Create_SubjectClaim_IsAutomationSubject()
    {
        var principal = CodeReviewAutomationIdentity.Create("org_123", null);

        await Assert
            .That(principal.FindFirstValue(OpenIddictConstants.Claims.Subject))
            .IsEqualTo("system:code-review-agent");
    }

    [Test]
    public async Task Create_OrganizationId_MatchesInput()
    {
        var principal = CodeReviewAutomationIdentity.Create("org_abc", null);

        await Assert.That(principal.FindFirstValue(AuthClaims.OrganizationId)).IsEqualTo("org_abc");
    }

    [Test]
    public async Task Create_WithTeamId_SetsTeamClaim()
    {
        var principal = CodeReviewAutomationIdentity.Create("org_123", "team_456");

        await Assert.That(principal.FindFirstValue(AuthClaims.TeamId)).IsEqualTo("team_456");
    }

    [Test]
    public async Task Create_WithoutTeamId_NoTeamClaim()
    {
        var principal = CodeReviewAutomationIdentity.Create("org_123", null);

        await Assert.That(principal.FindFirstValue(AuthClaims.TeamId)).IsNull();
    }

    [Test]
    public async Task Create_AsZeeqMinimalIdentity_RoundTripsCorrectly()
    {
        var principal = CodeReviewAutomationIdentity.Create("org_123", "team_456");

        var identity = principal.AsZeeqMinimalIdentity();

        await Assert.That(identity.OrganizationId).IsEqualTo("org_123");
        await Assert.That(identity.TeamId).IsEqualTo("team_456");
        await Assert.That(identity.OwnerUserId).IsEqualTo("system:code-review-agent");
    }
}
