using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Zeeq.Core.Identity;

/// <summary>
/// Deletes a long-lived user token from the browser management API.
/// </summary>
/// <remarks>
/// Deletion is enforced by <see cref="UserTokenValidationMiddleware"/> after
/// OpenIddict validates the token cryptographically. A missing metadata row rejects
/// subsequent requests that present the deleted token.
/// </remarks>
public sealed class RevokeUserTokenHandler(IZeeqIdentityStore identityStore) : IEndpointHandler
{
    /// <summary>
    /// Deletes the token only when it belongs to the authenticated owner.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string id,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var owner = AuthenticatedOwnerContext.FromClaimsPrincipal(user);
        var deleted = await identityStore.DeleteUserTokenAsync(id, owner.UserId, cancellationToken);

        if (!deleted)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.NoContent();
    }
}
