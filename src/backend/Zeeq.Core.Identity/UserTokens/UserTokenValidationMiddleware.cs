using Zeeq.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Core.Identity;

/// <summary>
/// Enforces local revocation for long-lived bearer tokens.
/// </summary>
/// <remarks>
/// OpenIddict validates the token cryptographically first. This middleware adds
/// the PoC-specific server-side metadata check for tokens that carry
/// <see cref="AuthClaims.UserTokenId" />.
/// </remarks>
public static class UserTokenValidationMiddleware
{
    /// <summary>
    /// Adds a post-OpenIddict validation step for tokens carrying <see cref="AuthClaims.UserTokenId"/>.
    /// </summary>
    /// <remarks>
    /// DCR and client-credentials tokens do not carry this claim and pass through.
    /// Long-lived user tokens must have an active local metadata row on every request
    /// so revocation and last-used tracking work even for self-contained access tokens.
    /// </remarks>
    public static IApplicationBuilder UseUserTokenValidation(this IApplicationBuilder app) =>
        app.Use(
            async (context, next) =>
            {
                var tokenId = context.User.FindFirst(AuthClaims.UserTokenId)?.Value;
                if (string.IsNullOrWhiteSpace(tokenId))
                {
                    // Normal interactive/DCR/client-credentials tokens are validated
                    // entirely by OpenIddict and do not participate in this local row check.
                    await next(context);
                    return;
                }

                var identityStore =
                    context.RequestServices.GetRequiredService<IZeeqIdentityStore>();
                var token = await identityStore.FindUserTokenAsync(tokenId, context.RequestAborted);

                var now = DateTimeOffset.UtcNow;
                if (
                    token is null
                    || token.RevokedAtUtc is not null
                    || token.ExpiresAtUtc <= now
                    || !string.Equals(
                        token.OwnerUserId,
                        context
                            .User.FindFirst(
                                OpenIddict.Abstractions.OpenIddictConstants.Claims.Subject
                            )
                            ?.Value,
                        StringComparison.Ordinal
                    )
                )
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await identityStore.MarkUserTokenUsedAsync(
                    tokenId,
                    token.OwnerUserId,
                    now,
                    context.RequestAborted
                );

                await next(context);
            }
        );
}
