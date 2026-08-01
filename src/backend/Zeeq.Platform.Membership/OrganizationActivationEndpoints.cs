using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Endpoints for exchanging activation-key provenance for the current inactive organization.
/// </summary>
/// <remarks>
/// This endpoint completes the first-login activation flow when organization
/// activation keys are enabled. The user has already authenticated and holds a
/// cookie for their newly-created personal organization, but that organization
/// is inactive until a valid key is exchanged.
///
/// The activation key is unassigned provenance, not an invitation and not an
/// organization-bound secret. The handler validates the key hash, claims it,
/// activates the current inactive organization, and refreshes activation state
/// used by the active-organization filters.
/// </remarks>
public sealed class OrganizationActivationEndpoints : IEndpoint
{
    /// <summary>
    /// Registers the organization activation exchange endpoint.
    /// </summary>
    /// <remarks>
    /// The route requires cookie authentication so the backend can trust the
    /// current user and organization claims. It intentionally does not require
    /// an active organization because the only purpose of this call is to make
    /// that organization active.
    /// </remarks>
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        app.MapPost(
                "me/organization-activation/exchange",
                static (
                    OrganizationActivationExchangeRequest request,
                    ClaimsPrincipal user,
                    [FromServices] ExchangeOrganizationActivationKeyHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(request, user, ct)
            )
            // Cookie auth is required for user/org identity, but active-org filters must
            // not run here because the target organization is inactive by definition.
            .RequireAuthorization(
                new AuthorizeAttribute
                {
                    AuthenticationSchemes = SetupIdentityExtension.CookieScheme,
                }
            )
            // Uses the fixed-window "organization-activation-exchange" policy defined in
            // SetupRateLimitingExtensions.AddZeeqRateLimiting. Activation-key exchange is
            // intentionally low volume: the policy allows a small number of attempts per
            // remote IP address with no queueing, enough for typos but not brute force.
            .RequireRateLimiting("organization-activation-exchange")
            .WithName("ExchangeOrganizationActivationKey")
            .WithTags("OrganizationActivation")
            .WithSummary("Activate organization.")
            .WithDescription(
                """
                Exchanges an unassigned activation key for activation of the authenticated
                user's current inactive organization. The key proves system-issued activation
                provenance; it is not pre-bound to an organization.

                Requires cookie authentication, but intentionally does not require an active
                organization because the target organization is not active yet. The route is
                rate limited and returns a generic validation failure when the key is invalid,
                expired, already used, revoked, or the current organization is not eligible.
                """
            );
    }
}
