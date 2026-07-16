using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Zeeq.Core.Identity;

/// <summary>
/// Lists long-lived user-token metadata for the authenticated browser user.
/// </summary>
/// <remarks>
/// The bearer token value is intentionally absent. Only creation returns the
/// token once; list views use local metadata for audit and management.
/// </remarks>
public sealed class ListUserTokensHandler(IZeeqIdentityStore identityStore) : IEndpointHandler
{
    /// <summary>
    /// Returns token summaries scoped to the owner resolved from server-issued claims.
    /// </summary>
    public async Task<Ok<IReadOnlyList<UserTokenSummary>>> HandleAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var owner = AuthenticatedOwnerContext.FromClaimsPrincipal(user);
        var tokens = await identityStore.ListUserTokensAsync(owner.UserId, cancellationToken);
        var response = tokens
            .Select(token => new UserTokenSummary(
                token.Id,
                token.DisplayName,
                token.CreatedAtUtc,
                token.ExpiresAtUtc,
                token.RevokedAtUtc,
                token.LastUsedAtUtc
            ))
            .ToArray();

        return TypedResults.Ok<IReadOnlyList<UserTokenSummary>>(response);
    }
}
