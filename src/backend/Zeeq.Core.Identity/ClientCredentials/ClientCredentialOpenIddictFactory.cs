using System.Security.Claims;
using Zeeq.Core.Models;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Builds OpenIddict objects for user-owned client credentials.
/// </summary>
/// <remarks>
/// OpenIddict owns the OAuth client application and token issuance pipeline.
/// The local <see cref="ClientCredential" /> row owns application-specific
/// metadata such as the creating user, active org/team context, and selected
/// content partitions.
/// </remarks>
public static class ClientCredentialOpenIddictFactory
{
    /// <summary>
    /// Creates the OpenIddict application used to authenticate a generated confidential client.
    /// </summary>
    /// <remarks>
    /// The generated client is intentionally limited to the token endpoint, the
    /// client-credentials grant, the MCP tools scope, and the configured MCP resource.
    /// Browser login, DCR setup, and user-token creation remain separate flows.
    /// </remarks>
    public static OpenIddictApplicationDescriptor CreateApplicationDescriptor(
        string clientId,
        string clientSecret,
        string displayName,
        AuthSettings settings
    ) =>
        new()
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + "mcp:tools",
                Permissions.Prefixes.Resource + settings.ResourceTrimmed,
            },
        };

    /// <summary>
    /// Creates the claims principal OpenIddict serializes into a client-credentials access token.
    /// </summary>
    /// <remarks>
    /// In the client-credentials flow, the OAuth actor is the confidential client.
    /// The token subject is therefore the client ID, while the local user who created
    /// that credential is preserved separately in <see cref="AuthClaims.OwnerUserId" />.
    /// </remarks>
    public static ClaimsPrincipal CreatePrincipal(
        ClientCredential credential,
        AuthSettings settings,
        IEnumerable<string>? scopes
    )
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role
        );

        // Client credentials represent a machine credential. Keep the OAuth subject
        // aligned to the authenticated client, and carry the human owner separately.
        identity.SetClaim(Claims.Subject, credential.ClientId);
        identity.SetClaim(AuthClaims.OwnerUserId, credential.OwnerUserId);

        // These context claims bind downstream MCP/API work to the same local
        // org/team/partition scope selected when the credential was created.
        identity.SetClaim(AuthClaims.OrganizationId, credential.OrganizationId);
        identity.SetClaim(AuthClaims.TeamId, credential.TeamId);
        identity.SetClaim(AuthClaims.PartitionIds, credential.SelectedPartitionIdsJson);

        // Provider claims are audit metadata for tracing the owner back to the
        // external IdP identity that created the local credential.
        identity.SetClaim(AuthClaims.Provider, credential.OwnerProvider);
        identity.SetClaim(AuthClaims.ProviderSubject, credential.OwnerProviderSubject);

        // Credential metadata supports diagnostics and future credential-level policy.
        identity.SetClaim(AuthClaims.AuthMode, GrantTypes.ClientCredentials);
        identity.SetClaim(AuthClaims.ClientCredentialId, credential.ClientId);
        identity.SetClaim(AuthClaims.ClientCredentialName, credential.DisplayName);
        identity.SetDestinations(_ => [Destinations.AccessToken]);

        var principal = new ClaimsPrincipal(identity);
        var effectiveScopes = (scopes ?? []).DefaultIfEmpty("mcp:tools");

        principal.SetScopes(effectiveScopes);
        principal.SetResources(settings.ResourceTrimmed);
        principal.SetAccessTokenLifetime(settings.ClientCredentialsAccessTokenLifetime);

        return principal;
    }
}
