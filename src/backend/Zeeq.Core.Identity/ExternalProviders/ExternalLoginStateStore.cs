using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace Zeeq.Core.Identity;

/// <summary>
/// Serialized OAuth state stored before redirecting to an external IdP.
/// </summary>
/// <remarks>
/// The state payload carries PKCE and return-url information across a browser
/// redirect. It must be consumed once on callback to prevent replay.
/// </remarks>
/// <param name="ProviderName">Configured provider key selected at login start.</param>
/// <param name="CodeVerifier">PKCE verifier paired with the authorization request challenge.</param>
/// <param name="RedirectUri">Callback URI sent to the external provider.</param>
/// <param name="ReturnUrl">Normalized local/trusted URL to resume after login.</param>
/// <param name="ExpiresAt">Hard expiry for the state entry.</param>
public sealed record OAuthState(
    string ProviderName,
    string CodeVerifier,
    string RedirectUri,
    string ReturnUrl,
    DateTimeOffset ExpiresAt
);

/// <summary>
/// One-time principal handoff from an API-origin OAuth callback to the browser app origin.
/// </summary>
/// <remarks>
/// Google can callback to localhost while the browser cookie has to be scoped to
/// the Vue app host. This handoff stores the completed local principal briefly so
/// <c>/auth/complete/{provider}</c> can issue the cookie on the frontend origin.
/// </remarks>
/// <param name="Principal">Local cookie principal created from the verified IdP identity.</param>
/// <param name="ReturnUrl">Normalized return URL to redirect to after issuing the cookie.</param>
/// <param name="ExpiresAt">Hard expiry for the handoff entry.</param>
public sealed record AuthHandoff(
    System.Security.Claims.ClaimsPrincipal Principal,
    string ReturnUrl,
    DateTimeOffset ExpiresAt
);

/// <summary>
/// Short-lived one-time store for external IdP OAuth state.
/// </summary>
public sealed class ExternalLoginStateStore(IZeeqAuthStateStore stateStore)
{
    private const string Purpose = "oauth_state";

    /// <summary>
    /// Stores external OAuth state under the provider-generated state key.
    /// </summary>
    public Task StoreAsync(string state, OAuthState value, CancellationToken cancellationToken)
    {
        return stateStore.StoreAsync(
            Purpose,
            state,
            JsonSerializer.Serialize(value),
            value.ExpiresAt,
            cancellationToken
        );
    }

    /// <summary>
    /// Atomically consumes the OAuth state entry for a provider callback.
    /// </summary>
    public async Task<OAuthState?> ConsumeAsync(string state, CancellationToken cancellationToken)
    {
        var payload = await stateStore.ConsumeAsync(Purpose, state, cancellationToken);

        return payload is null ? null : JsonSerializer.Deserialize<OAuthState>(payload);
    }
}

/// <summary>
/// Short-lived handoff used when an OAuth provider must callback on localhost
/// but the browser cookie must be issued on the Vue app origin.
/// </summary>
public sealed class AuthHandoffStore(IZeeqAuthStateStore stateStore)
{
    private const string Purpose = "auth_handoff";

    /// <summary>
    /// Stores a one-time browser handoff and returns the opaque ticket.
    /// </summary>
    public Task<string> StoreAsync(AuthHandoff value, CancellationToken cancellationToken)
    {
        var ticket = Guid.NewGuid().ToString("N");
        var authenticationTicket = new AuthenticationTicket(
            value.Principal,
            SetupIdentityExtension.CookieScheme
        );
        var serialized = new SerializedAuthHandoff(
            Convert.ToBase64String(TicketSerializer.Default.Serialize(authenticationTicket)),
            value.ReturnUrl,
            value.ExpiresAt
        );

        return StoreAndReturnTicketAsync(ticket, serialized, value.ExpiresAt, cancellationToken);
    }

    /// <summary>
    /// Consumes the one-time handoff ticket and reconstructs the local principal.
    /// </summary>
    public async Task<AuthHandoff?> ConsumeAsync(string ticket, CancellationToken cancellationToken)
    {
        var payload = await stateStore.ConsumeAsync(Purpose, ticket, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        var serialized = JsonSerializer.Deserialize<SerializedAuthHandoff>(payload);
        if (serialized is null)
        {
            return null;
        }

        var authenticationTicket = TicketSerializer.Default.Deserialize(
            Convert.FromBase64String(serialized.PrincipalTicket)
        );

        return authenticationTicket is null
            ? null
            : new AuthHandoff(
                authenticationTicket.Principal,
                serialized.ReturnUrl,
                serialized.ExpiresAt
            );
    }

    private async Task<string> StoreAndReturnTicketAsync(
        string ticket,
        SerializedAuthHandoff value,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken
    )
    {
        await stateStore.StoreAsync(
            Purpose,
            ticket,
            JsonSerializer.Serialize(value),
            expiresAtUtc,
            cancellationToken
        );

        return ticket;
    }

    private sealed record SerializedAuthHandoff(
        string PrincipalTicket,
        string ReturnUrl,
        DateTimeOffset ExpiresAt
    );
}
