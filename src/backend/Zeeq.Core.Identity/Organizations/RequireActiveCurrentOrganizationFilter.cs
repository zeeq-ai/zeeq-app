using System.Security.Claims;
using Zeeq.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Zeeq.Core.Identity;

/// <summary>
/// Blocks current-user endpoints when the active organization in the session is inactive.
/// </summary>
/// <remarks>
/// This filter is intentionally separate from <see cref="RequireActiveOrganizationFilter" />
/// because <c>/me</c> has no route <c>orgId</c>. It reads the same cookie claim used by the
/// application shell and applies the same 30-second activation-state cache.
/// </remarks>
public sealed partial class RequireActiveCurrentOrganizationFilter(
    HybridCache cache,
    IZeeqMembershipStore store,
    AuthSettings settings,
    ILogger<RequireActiveCurrentOrganizationFilter> logger
) : IEndpointFilter
{
    private const string CacheKeyPrefix = "identity:organization-activation-state:";

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var orgId = context.HttpContext.User.AsZeeqMinimalIdentity().OrganizationId;

        if (string.IsNullOrWhiteSpace(orgId))
        {
            LogMissingCurrentOrganization(logger);
            return TypedResults.Unauthorized();
        }

        var state = await cache.GetOrCreateAsync<
            (IZeeqMembershipStore Store, string OrganizationId),
            OrganizationActivationState?
        >(
            key: CacheKeyPrefix + orgId,
            state: (store, orgId),
            factory: static async (state, cancellationToken) =>
                await state.Store.FindOrganizationActivationStateAsync(
                    state.OrganizationId,
                    cancellationToken
                ),
            options: CacheOptions,
            cancellationToken: context.HttpContext.RequestAborted
        );

        if (state is null)
        {
            LogMissingOrganization(logger, orgId);
            return TypedResults.NotFound();
        }

        if (!state.IsActive)
        {
            LogInactiveOrganizationBlocked(logger, orgId);
            return TypedResults.Redirect(
                $"{settings.FrontendBaseUriTrimmed}/login?inactiveOrg=true",
                permanent: false,
                preserveMethod: false
            );
        }

        return await next(context);
    }

    [LoggerMessage(
        EventId = 2610,
        Level = LogLevel.Warning,
        Message = "Current-organization active filter ran without an active organization claim."
    )]
    private static partial void LogMissingCurrentOrganization(ILogger logger);

    [LoggerMessage(
        EventId = 2611,
        Level = LogLevel.Warning,
        Message = "Current organization {OrganizationId} was not found during active organization enforcement."
    )]
    private static partial void LogMissingOrganization(ILogger logger, string organizationId);

    [LoggerMessage(
        EventId = 2612,
        Level = LogLevel.Information,
        Message = "Inactive current organization {OrganizationId} was redirected to activation."
    )]
    private static partial void LogInactiveOrganizationBlocked(
        ILogger logger,
        string organizationId
    );
}
