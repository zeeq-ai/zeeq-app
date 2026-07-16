using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Builds the internal OpenIddict client used by the user-token management flow.
/// </summary>
/// <remarks>
/// Long-lived user tokens are still issued by OpenIddict, but the browser
/// management endpoint starts that issuance with a server-side back-channel
/// token request. This confidential client authenticates that call and replaces
/// the earlier PoC dependency on anonymous token clients.
/// </remarks>
public static class UserTokenInternalClientFactory
{
    /// <summary>
    /// Creates the confidential OpenIddict application allowed to use the custom user-token grant.
    /// </summary>
    public static OpenIddictApplicationDescriptor CreateApplicationDescriptor(AuthSettings settings)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = settings.InternalUserTokenClientId,
            ClientSecret = settings.InternalUserTokenClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Internal user token issuer",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.Prefixes.Scope + "mcp:tools",
                Permissions.Prefixes.Resource + settings.ResourceTrimmed,
            },
        };

        // Custom grants use extension grant-type permission strings, so let
        // OpenIddict build the right permission value instead of hard-coding it.
        descriptor.AddGrantTypePermissions(UserTokenGrantHandler.GrantType);

        return descriptor;
    }
}
