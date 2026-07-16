using Zeeq.Core.Identity;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity.Tests;

public class ExternalUserPrincipalFactoryTests
{
    [Test]
    public async Task CreateCookiePrincipal_WithOrgRole_UsesConfiguredRoleClaimType()
    {
        var authContext = new AuthContext("usr_123", "org_123", "team_123");

        var principal = ExternalUserPrincipalFactory.CreateCookiePrincipal(
            authContext,
            provider: "mock",
            providerSubject: "mock_123",
            name: "Test User",
            email: "test@example.com",
            pictureUrl: null,
            orgSlug: "test-org",
            orgRole: "owner"
        );

        await Assert.That(principal.Identity?.IsAuthenticated).IsTrue();
        await Assert
            .That(principal.Claims.Any(c => c.Type == OpenIddictConstants.Claims.Role))
            .IsTrue();
        await Assert.That(principal.IsInRole("owner")).IsTrue();
    }

    [Test]
    public async Task CreateCookiePrincipal_WithSystemAdmin_AddsReservedRoleClaim()
    {
        var authContext = new AuthContext("usr_123", "org_123", "team_123");

        var principal = ExternalUserPrincipalFactory.CreateCookiePrincipal(
            authContext,
            provider: "google",
            providerSubject: "sub_123",
            name: "Test User",
            email: "test@example.com",
            pictureUrl: null,
            orgSlug: "test-org",
            orgRole: "owner",
            isSystemAdmin: true
        );

        await Assert.That(principal.IsInRole("owner")).IsTrue();
        await Assert.That(principal.IsInRole(SystemRoles.SystemAdmin)).IsTrue();
    }
}
