using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Common.AspNetCore.Contracts;

namespace Zeeq.Core.Identity;

/// <summary>
/// Replaces the signed-in user's aliases for one organization.
/// </summary>
public sealed class UpdateUserAliasesHandler(IZeeqIdentityStore identityStore) : IEndpointHandler
{
    internal async Task<
        Results<
            UnauthorizedHttpResult,
            BadRequest<IdentityEndpointError>,
            Conflict<IdentityEndpointError>,
            Ok<UserAliasesResponse>
        >
    > HandleAsync(
        string organizationId,
        UpdateUserAliasesRequest request,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var userId = user.AsZeeqIdentity().OwnerUserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return TypedResults.Unauthorized();
        }

        IReadOnlyList<UserAliasWrite> aliases;
        try
        {
            if (request.EmailAliases is null || request.GitHubAliases is null)
            {
                return TypedResults.BadRequest(
                    new IdentityEndpointError(
                        "invalid_alias",
                        "Email aliases and GitHub aliases are required arrays."
                    )
                );
            }

            aliases = UserAliasNormalizer.ToWrites(request.EmailAliases, request.GitHubAliases);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new IdentityEndpointError("invalid_alias", ex.Message));
        }

        try
        {
            var saved = await identityStore.ReplaceUserAliasesAsync(
                organizationId,
                userId,
                aliases,
                cancellationToken
            );

            return TypedResults.Ok(
                new UserAliasesResponse(saved.Select(UserAliasEndpointMapping.ToDto).ToArray())
            );
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Conflict(new IdentityEndpointError("alias_conflict", ex.Message));
        }
    }
}
