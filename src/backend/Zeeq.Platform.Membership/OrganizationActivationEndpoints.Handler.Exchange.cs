using Microsoft.Extensions.Caching.Hybrid;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Exchanges a valid activation key for organization activation.
/// </summary>
/// <remarks>
/// This handler runs after the login flow has produced a cookie-authenticated
/// session for a user's newly-created personal organization. That organization
/// can still be inactive, so the handler relies on the session claims for user
/// and organization identity instead of active-organization filters.
///
/// Invalid, expired, revoked, already-used, or ineligible exchange attempts all
/// return the same validation problem. That preserves a small response surface
/// for activation-key probing while still giving the frontend a clear form
/// error to display.
/// </remarks>
public sealed class ExchangeOrganizationActivationKeyHandler(
    IOrganizationActivationKeyStore store,
    AppSettings appSettings,
    HybridCache cache
) : IEndpointHandler
{
    /// <summary>
    /// Activates the current inactive organization when the key is valid.
    /// </summary>
    /// <remarks>
    /// When activation keys are disabled, this endpoint behaves as absent so
    /// the existing first-login flow is unchanged. On successful exchange, the
    /// active-organization cache entry is evicted so subsequent requests see
    /// the organization as active immediately.
    /// </remarks>
    public async Task<
        Results<
            Ok<OrganizationActivationExchangeResponse>,
            ValidationProblem,
            ProblemHttpResult,
            NotFound
        >
    > HandleAsync(
        OrganizationActivationExchangeRequest request,
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        // Preserve legacy behavior when activation keys are disabled: no
        // activation exchange route is available to the frontend.
        if (!appSettings.Platform.OrganizationActivationKeysEnabled)
        {
            return TypedResults.NotFound();
        }

        // Reject malformed input before hashing so low-effort probes never
        // reach the store's consume-and-activate path.
        if (!OrganizationActivationKeyMaterial.IsValidKeyFormat(request.Key))
        {
            return InvalidActivationRequest();
        }

        var userId = user.FindFirstValue(OpenIddictConstants.Claims.Subject);
        var organizationId = user.FindFirstValue(AuthClaims.OrganizationId);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(organizationId))
        {
            return TypedResults.Problem(
                title: "Missing activation context.",
                detail: "The signed-in session does not include organization activation claims.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        // The store claims the key and activates the organization atomically.
        // The handler only exposes whether the exchange succeeded.
        var result = await store.ConsumeKeyAndActivateOrganizationAsync(
            OrganizationActivationKeyMaterial.ComputeHash(request.Key),
            organizationId,
            userId,
            ct
        );

        if (result != OrganizationActivationExchangeResult.Activated)
        {
            return InvalidActivationRequest();
        }

        // Active-organization filters cache activation state; clear it so the
        // next authenticated request observes the newly activated organization.
        await cache.RemoveAsync(
            OrganizationActivationCacheKeys.ForOrganization(organizationId),
            ct
        );

        return TypedResults.Ok(new OrganizationActivationExchangeResponse(organizationId));
    }

    private static ValidationProblem InvalidActivationRequest() =>
        TypedResults.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["activation"] = ["The activation token or key is invalid."],
            }
        );
}
