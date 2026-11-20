using System.Security.Claims;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity.Tests;

public sealed class LibraryAudienceTests
{
    [Test]
    [Arguments("owner", true)]
    [Arguments("admin", true)]
    [Arguments("member", false)]
    public async Task RoleAppliesOnlyToTheClaimOrganization(string role, bool administrator)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new(OpenIddictConstants.Claims.Subject, "user"),
                    new(AuthClaims.OrganizationId, "org-a"),
                    new(ClaimTypes.Role, role),
                ],
                authenticationType: "test"
            )
        );

        await Assert
            .That(principal.GetLibraryAudience("org-a")!.IsAdministrator)
            .IsEqualTo(administrator);
        await Assert.That(principal.GetLibraryAudience("org-b")).IsNull();
    }
}
