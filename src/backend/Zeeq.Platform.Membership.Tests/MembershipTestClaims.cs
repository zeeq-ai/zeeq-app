using System.Security.Claims;
using OpenIddict.Abstractions;

namespace Zeeq.Platform.Membership.Tests;

/// <summary>
/// Builds authenticated test principals for membership handler tests.
/// </summary>
internal static class MembershipTestClaims
{
    public static ClaimsPrincipal TestUser(string userId, string email = "user@test.com")
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.Subject, userId),
                new Claim(OpenIddictConstants.Claims.Email, email),
                new Claim(ClaimTypes.Email, email),
            ],
            "test"
        );

        return new ClaimsPrincipal(identity);
    }
}
