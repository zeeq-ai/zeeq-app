using Zeeq.Core.Models;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Zeeq.Core.Identity;

/// <summary>
/// Issues long-lived user-owned access tokens from one-time browser tickets.
/// </summary>
/// <remarks>
/// The browser management endpoint creates the local metadata row and stores a
/// short-lived ticket. This OpenIddict token endpoint handler consumes that
/// ticket and lets OpenIddict serialize the final access token as JWE or JWS.
/// </remarks>
public sealed partial class UserTokenGrantHandler(
    UserTokenGrantTicketStore ticketStore,
    IZeeqIdentityStore identityStore,
    AuthSettings settings,
    ILogger<UserTokenGrantHandler> log
) : IOpenIddictServerHandler<OpenIddictServerEvents.HandleTokenRequestContext>
{
    /// <summary>
    /// Custom OpenIddict grant used only by the server-side browser management flow.
    /// </summary>
    public const string GrantType = "urn:auth:grant-type:user_token";

    /// <summary>
    /// Form parameter carrying the one-time ticket consumed by this handler.
    /// </summary>
    public const string TicketParameter = "auth_user_token_ticket";

    /// <summary>
    /// Consumes the one-time ticket and signs in an OpenIddict principal for token serialization.
    /// </summary>
    public async ValueTask HandleAsync(OpenIddictServerEvents.HandleTokenRequestContext context)
    {
        if (!string.Equals(context.Request.GrantType, GrantType, StringComparison.Ordinal))
        {
            return;
        }

        var ticketValue = (string?)context.Request[TicketParameter];
        if (string.IsNullOrWhiteSpace(ticketValue))
        {
            context.Reject(
                error: Errors.InvalidRequest,
                description: $"The {TicketParameter} parameter is required."
            );
            return;
        }

        var ticket = await ticketStore.ConsumeAsync(ticketValue, context.CancellationToken);
        if (ticket is null || ticket.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            LogTicketMissing();
            context.Reject(
                error: Errors.InvalidGrant,
                description: "The user token grant ticket is invalid or expired."
            );
            return;
        }

        var token = await identityStore.FindUserTokenAsync(
            ticket.TokenId,
            context.CancellationToken
        );
        if (token is null || token.RevokedAtUtc is not null)
        {
            LogTokenMissing(ticket.TokenId);
            context.Reject(
                error: Errors.InvalidGrant,
                description: "The user token record is not active."
            );
            return;
        }

        var principal = UserTokenOpenIddictFactory.CreatePrincipal(
            ticket.OwnerPrincipal,
            token,
            settings
        );

        LogTokenIssued(token.Id, token.OwnerUserId);
        context.SignIn(principal);
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "User token grant rejected because the one-time ticket was missing or expired."
    )]
    private partial void LogTicketMissing();

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Warning,
        Message = "User token grant rejected because TokenId={TokenId} is not active."
    )]
    private partial void LogTokenMissing(string tokenId);

    [LoggerMessage(
        EventId = 1302,
        Level = LogLevel.Information,
        Message = "Issued long-lived user token. TokenId={TokenId}, OwnerUserId={OwnerUserId}."
    )]
    private partial void LogTokenIssued(string tokenId, string ownerUserId);
}
