using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Zeeq.Core.Identity;

/// <summary>
/// Creates local metadata and asks OpenIddict to issue a long-lived user token.
/// </summary>
/// <remarks>
/// The sandbox prototype proved that returning an OpenIddict sign-in result from
/// an arbitrary management endpoint fails because no token endpoint transaction
/// exists. This handler therefore creates a short-lived one-time ticket and then
/// performs a server-side exchange against <c>/connect/token</c> using the custom
/// user-token grant.
/// </remarks>
public sealed partial class CreateUserTokenHandler(
    IZeeqIdentityStore identityStore,
    AuthSettings settings,
    UserTokenGrantTicketStore ticketStore,
    IHttpClientFactory httpClientFactory,
    ILogger<CreateUserTokenHandler> logger
) : IEndpointHandler
{
    /// <summary>
    /// Validates the request, stores metadata, exchanges the grant ticket, and streams the token response.
    /// </summary>
    /// <remarks>
    /// If the OpenIddict exchange fails, the local metadata row is deleted so the
    /// user does not see an unusable token entry in later list responses.
    /// </remarks>
    public async Task<IResult> HandleAsync(
        UserTokenCreateRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var displayName = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    [nameof(request.DisplayName)] = ["Display name is required."],
                }
            );
        }

        var lifetimeDays = request.ExpiresInDays ?? settings.UserTokenDefaultLifetimeDays;
        if (lifetimeDays <= 0 || lifetimeDays > settings.UserTokenMaxLifetimeDays)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    [nameof(request.ExpiresInDays)] =
                    [
                        $"Expiration must be between 1 and {settings.UserTokenMaxLifetimeDays} days.",
                    ],
                }
            );
        }

        var owner = AuthenticatedOwnerContext.FromClaimsPrincipal(user);
        var createdAt = DateTimeOffset.UtcNow;
        var token = new UserToken
        {
            Id = "auth_tok_" + Guid.NewGuid().ToString("N"),
            CreatedAtUtc = createdAt,
            DisplayName = displayName,
            ExpiresAtUtc = createdAt.AddDays(lifetimeDays),
            OrganizationId = owner.OrganizationId,
            OwnerProvider = owner.Provider,
            OwnerProviderSubject = owner.ProviderSubject,
            OwnerUserId = owner.UserId,
            SelectedPartitionIdsJson = owner.PartitionIdsJson,
            TeamId = owner.TeamId,
        };

        await identityStore.AddUserTokenAsync(token, cancellationToken);

        LogCreatedToken(logger, token.Id, owner.UserId, displayName, token.ExpiresAtUtc);

        // The ticket is consumed by UserTokenGrantHandler inside OpenIddict's token
        // endpoint, which is the only place OpenIddict can serialize a token response.
        var ticket = await ticketStore.StoreAsync(
            new UserTokenGrantTicket(token.Id, user, createdAt.AddMinutes(2)),
            cancellationToken
        );

        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            settings.IssuerTrimmed + "/connect/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    [OpenIddict.Abstractions.OpenIddictConstants.Parameters.ClientId] =
                        settings.InternalUserTokenClientId,
                    [OpenIddict.Abstractions.OpenIddictConstants.Parameters.ClientSecret] =
                        settings.InternalUserTokenClientSecret,
                    [OpenIddict.Abstractions.OpenIddictConstants.Parameters.GrantType] =
                        UserTokenGrantHandler.GrantType,
                    [UserTokenGrantHandler.TicketParameter] = ticket,
                    [OpenIddict.Abstractions.OpenIddictConstants.Parameters.Scope] = "mcp:tools",
                    [OpenIddict.Abstractions.OpenIddictConstants.Parameters.Resource] =
                        settings.ResourceTrimmed,
                }
            ),
            cancellationToken
        );
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await identityStore.RemoveUserTokenAsync(token.Id, cancellationToken);

            LogTokenExchangeFailed(logger, token.Id, response.StatusCode);
        }

        return TypedResults.Content(
            responseBody,
            response.Content.Headers.ContentType?.ToString() ?? "application/json",
            statusCode: (int)response.StatusCode
        );
    }

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "Created long-lived user token. TokenId={TokenId}, OwnerUserId={OwnerUserId}, DisplayName={DisplayName}, ExpiresAtUtc={ExpiresAtUtc}"
    )]
    private static partial void LogCreatedToken(
        ILogger logger,
        string tokenId,
        string ownerUserId,
        string displayName,
        DateTimeOffset expiresAtUtc
    );

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "Long-lived user token exchange failed. TokenId={TokenId}, StatusCode={StatusCode}"
    )]
    private static partial void LogTokenExchangeFailed(
        ILogger logger,
        string tokenId,
        System.Net.HttpStatusCode statusCode
    );
}
