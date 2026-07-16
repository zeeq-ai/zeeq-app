using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace Zeeq.Core.Identity;

/// <summary>
/// One-time bridge ticket used by the browser management endpoint to enter the token endpoint.
/// </summary>
/// <remarks>
/// The ticket carries the already-authenticated browser principal and local token
/// metadata ID. It is intentionally short-lived and consumed once so the custom
/// grant cannot be replayed to mint additional bearer tokens.
/// </remarks>
/// <param name="TokenId">Local user-token metadata row to bind to the issued access token.</param>
/// <param name="OwnerPrincipal">Authenticated browser principal that requested token creation.</param>
/// <param name="ExpiresAtUtc">Hard expiry for the ticket.</param>
public sealed record UserTokenGrantTicket(
    string TokenId,
    ClaimsPrincipal OwnerPrincipal,
    DateTimeOffset ExpiresAtUtc
);

/// <summary>
/// Short-lived one-time store that bridges the browser management endpoint to
/// the OpenIddict token endpoint for long-lived user token creation.
/// </summary>
public sealed class UserTokenGrantTicketStore(IZeeqAuthStateStore stateStore)
{
    private const string Purpose = "user_token_grant_ticket";

    /// <summary>
    /// Stores the one-time ticket and returns an opaque grant value.
    /// </summary>
    public Task<string> StoreAsync(UserTokenGrantTicket value, CancellationToken cancellationToken)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var authenticationTicket = new AuthenticationTicket(
            value.OwnerPrincipal,
            SetupIdentityExtension.CookieScheme
        );
        var serialized = new SerializedUserTokenGrantTicket(
            value.TokenId,
            Convert.ToBase64String(TicketSerializer.Default.Serialize(authenticationTicket)),
            value.ExpiresAtUtc
        );

        return StoreAndReturnTicketAsync(ticket, serialized, value.ExpiresAtUtc, cancellationToken);
    }

    /// <summary>
    /// Atomically consumes a one-time user-token grant ticket.
    /// </summary>
    public async Task<UserTokenGrantTicket?> ConsumeAsync(
        string ticket,
        CancellationToken cancellationToken
    )
    {
        var payload = await stateStore.ConsumeAsync(Purpose, ticket, cancellationToken);
        if (payload is null)
        {
            return null;
        }

        var serialized = JsonSerializer.Deserialize<SerializedUserTokenGrantTicket>(payload);
        if (serialized is null)
        {
            return null;
        }

        var authenticationTicket = TicketSerializer.Default.Deserialize(
            Convert.FromBase64String(serialized.OwnerPrincipalTicket)
        );

        return authenticationTicket is null
            ? null
            : new UserTokenGrantTicket(
                serialized.TokenId,
                authenticationTicket.Principal,
                serialized.ExpiresAtUtc
            );
    }

    private async Task<string> StoreAndReturnTicketAsync(
        string ticket,
        SerializedUserTokenGrantTicket value,
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

    private sealed record SerializedUserTokenGrantTicket(
        string TokenId,
        string OwnerPrincipalTicket,
        DateTimeOffset ExpiresAtUtc
    );
}
