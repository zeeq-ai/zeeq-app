using Zeeq.Core.Models;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Issues access tokens for user-owned client credentials.
/// </summary>
/// <remarks>
/// OpenIddict validates the confidential OAuth client and its secret before
/// this handler runs. This handler then maps the authenticated client ID to the
/// local <see cref="ClientCredential" /> row so tokens carry the owner
/// identity created by the external IdP login flow.
/// </remarks>
public sealed partial class ClientCredentialsTokenHandler(
    IZeeqIdentityStore identityStore,
    AuthSettings settings,
    ILogger<ClientCredentialsTokenHandler> log
) : IOpenIddictServerHandler<OpenIddictServerEvents.HandleTokenRequestContext>
{
    /// <summary>
    /// Handles client-credentials grant requests after OpenIddict validates the client secret.
    /// </summary>
    /// <remarks>
    /// The token request must not provide owner identity. Ownership is recovered
    /// by mapping the validated <c>client_id</c> to the local
    /// <see cref="ClientCredential"/> row created through the browser management API.
    /// </remarks>
    public async ValueTask HandleAsync(OpenIddictServerEvents.HandleTokenRequestContext context)
    {
        if (
            !string.Equals(
                context.Request.GrantType,
                GrantTypes.ClientCredentials,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(context.Request.ClientId))
        {
            context.Reject(
                error: Errors.InvalidClient,
                description: "The client_id parameter is required."
            );
            return;
        }

        var credential = await identityStore.FindClientCredentialAsync(
            context.Request.ClientId,
            context.CancellationToken
        );

        if (credential is null)
        {
            LogCredentialMissing(context.Request.ClientId);
            context.Reject(
                error: Errors.InvalidClient,
                description: "The client credential is not registered."
            );
            return;
        }

        if (credential.RevokedAtUtc is not null)
        {
            LogCredentialRevoked(context.Request.ClientId);
            context.Reject(
                error: Errors.InvalidClient,
                description: "The client credential has been revoked."
            );
            return;
        }

        var principal = ClientCredentialOpenIddictFactory.CreatePrincipal(
            credential,
            settings,
            context.Request.GetScopes()
        );

        LogCredentialTokenIssued(credential.ClientId, credential.OwnerUserId);
        context.SignIn(principal);
    }

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "Client credentials token request rejected because ClientId={ClientId} has no local credential row."
    )]
    private partial void LogCredentialMissing(string clientId);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Client credentials token request rejected because ClientId={ClientId} is revoked."
    )]
    private partial void LogCredentialRevoked(string clientId);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Information,
        Message = "Issued client credentials token for ClientId={ClientId}, OwnerUserId={OwnerUserId}."
    )]
    private partial void LogCredentialTokenIssued(string clientId, string ownerUserId);
}
