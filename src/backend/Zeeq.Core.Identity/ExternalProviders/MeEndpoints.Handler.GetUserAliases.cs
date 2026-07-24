using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Common.AspNetCore.Contracts;

namespace Zeeq.Core.Identity;

/// <summary>
/// Reads the signed-in user's aliases for one organization.
/// </summary>
public sealed class GetUserAliasesHandler(IZeeqIdentityStore identityStore) : IEndpointHandler
{
    internal async Task<Results<UnauthorizedHttpResult, Ok<UserAliasesResponse>>> HandleAsync(
        string organizationId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var userId = user.AsZeeqIdentity().OwnerUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.Unauthorized();
        }

        var aliases = await identityStore.ListUserAliasesAsync(
            organizationId,
            userId,
            cancellationToken
        );

        return TypedResults.Ok(
            new UserAliasesResponse(aliases.Select(UserAliasEndpointMapping.ToDto).ToArray())
        );
    }
}
