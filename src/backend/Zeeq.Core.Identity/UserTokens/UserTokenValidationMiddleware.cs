using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Enforces local revocation for long-lived bearer tokens.
/// </summary>
/// <remarks>
/// <para>
/// OpenIddict validates the token cryptographically first. This middleware adds
/// the PoC-specific server-side metadata check for tokens that carry
/// <see cref="AuthClaims.UserTokenId" />.
/// </para>
/// <para>
/// This middleware also rejects a request whose organization membership is no
/// longer active (in addition to the existing revoked/expired/owner-mismatch
/// checks), backed by a <see cref="MembershipActivationCacheKeys"/>-keyed
/// <c>HybridCache</c> lookup. It rejects here — precisely because bearer-token
/// callers are typically non-interactive (scripts, CI, MCP tool integrations):
/// there is no user at a keyboard to route to <c>/me</c> or a re-login flow, so
/// failing the request immediately is the only correct recovery signal. The
/// caller must go mint a fresh token through a human with current access, not
/// be silently let through.
/// </para>
/// <para>
/// <see cref="MembershipEnrichmentMiddleware"/>, which runs immediately after
/// this one in the pipeline (<c>Program.cs</c>), intentionally does
/// <b>not</b> perform the same rejection for the broader set of authenticated
/// traffic it covers (cookie/session included)
/// </para>
/// </remarks>
public static partial class UserTokenValidationMiddleware
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

                var membershipStore =
                    context.RequestServices.GetRequiredService<IZeeqMembershipStore>();
                var membershipState = await membershipStore.FindMembershipActivationStateAsync(
                    token.OrganizationId,
                    token.OwnerUserId,
                    context.RequestAborted
                );

                if (membershipState is null || !membershipState.IsActive)
                {
                    RejectInactiveMembership(context, tokenId, token);
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

    /// <summary>
    /// Logs and rejects a request whose token is otherwise valid but whose
    /// organization membership is missing or no longer active.
    /// </summary>
    private static void RejectInactiveMembership(
        HttpContext context,
        string tokenId,
        UserToken token
    )
    {
        var logger = context
            .RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(UserTokenValidationMiddleware).FullName!);
        LogMembershipInactive(logger, tokenId, token.OrganizationId, token.OwnerUserId);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    [LoggerMessage(
        EventId = 1400,
        Level = LogLevel.Warning,
        Message = "Rejected user token for inactive organization membership. TokenId={TokenId}, OrganizationId={OrganizationId}, OwnerUserId={OwnerUserId}"
    )]
    private static partial void LogMembershipInactive(
        ILogger logger,
        string tokenId,
        string organizationId,
        string ownerUserId
    );
}
