using Zeeq.Core.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Zeeq.Core.Identity;

/// <summary>
/// Blocks route-scoped mutation endpoints when the referenced organization is inactive.
/// </summary>
/// <remarks>
/// The filter reads the normalized <c>{orgId}</c> route value, caches a narrow activation
/// projection for 30 seconds, and short-circuits inactive organizations to the activation UI.
/// Register <see cref="RequireRouteOrganizationMatchesCookieFilter" /> before this filter.
/// </remarks>
public sealed partial class RequireActiveOrganizationFilter(
    HybridCache cache,
    IZeeqMembershipStore store,
    AuthSettings settings,
    ILogger<RequireActiveOrganizationFilter> logger
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
        var orgId = context.HttpContext.Request.RouteValues["orgId"]?.ToString();

        if (string.IsNullOrWhiteSpace(orgId))
        {
            LogMissingRouteOrganization(logger);
            return TypedResults.BadRequest();
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
        EventId = 2600,
        Level = LogLevel.Warning,
        Message = "Route-scoped active organization filter ran without an orgId route value."
    )]
    private static partial void LogMissingRouteOrganization(ILogger logger);

    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Warning,
        Message = "Organization {OrganizationId} was not found during active organization enforcement."
    )]
    private static partial void LogMissingOrganization(ILogger logger, string organizationId);

    [LoggerMessage(
        EventId = 2602,
        Level = LogLevel.Information,
        Message = "Inactive organization {OrganizationId} was redirected to activation."
    )]
    private static partial void LogInactiveOrganizationBlocked(
        ILogger logger,
        string organizationId
    );
}
