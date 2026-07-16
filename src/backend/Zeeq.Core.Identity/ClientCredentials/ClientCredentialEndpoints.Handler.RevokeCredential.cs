using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity;

/// <summary>
/// Deletes a user-owned client credential from the browser management API.
/// </summary>
/// <remarks>
/// Deletion removes the local ownership row and the matching OpenIddict application,
/// preventing future token issuance. Already-issued self-contained access tokens remain
/// valid until their normal expiry window.
/// </remarks>
public sealed class RevokeClientCredentialHandler(
    IZeeqIdentityStore identityStore,
    IOpenIddictApplicationManager applicationManager
) : IEndpointHandler
{
    /// <summary>
    /// Deletes the credential only when it belongs to the authenticated owner.
    /// </summary>
    public async Task<Results<NoContent, NotFound>> HandleAsync(
        string clientId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var owner = AuthenticatedOwnerContext.FromClaimsPrincipal(user);

        var deleted = await identityStore.DeleteClientCredentialAsync(
            clientId,
            owner.UserId,
            cancellationToken
        );

        if (!deleted)
        {
            return TypedResults.NotFound();
        }

        var application = await applicationManager.FindByClientIdAsync(clientId, cancellationToken);
        if (application is not null)
        {
            await applicationManager.DeleteAsync(application, cancellationToken);
        }

        return TypedResults.NoContent();
    }
}
