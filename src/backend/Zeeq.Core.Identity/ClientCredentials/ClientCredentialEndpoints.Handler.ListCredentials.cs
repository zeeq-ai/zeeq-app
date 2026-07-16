using System.Security.Claims;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Zeeq.Core.Identity;

/// <summary>
/// Lists local metadata for client credentials owned by the authenticated browser user.
/// </summary>
/// <remarks>
/// The local metadata row is the management view for credentials. It intentionally
/// omits the client secret because OpenIddict validates that secret during token exchange.
/// </remarks>
public sealed class ListClientCredentialsHandler(IZeeqIdentityStore identityStore)
    : IEndpointHandler
{
    /// <summary>
    /// Returns credential summaries scoped to the owner resolved from server-issued claims.
    /// </summary>
    public async Task<Ok<IReadOnlyList<ClientCredentialSummary>>> HandleAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var owner = AuthenticatedOwnerContext.FromClaimsPrincipal(user);

        var credentials = await identityStore.ListClientCredentialsAsync(
            owner.UserId,
            cancellationToken
        );

        var response = credentials
            .Select(credential => new ClientCredentialSummary(
                credential.ClientId,
                credential.DisplayName,
                credential.CreatedAtUtc,
                credential.RevokedAtUtc
            ))
            .ToArray();

        return TypedResults.Ok<IReadOnlyList<ClientCredentialSummary>>(response);
    }
}
