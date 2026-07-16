using System.Security.Claims;
using OpenIddict.Abstractions;

namespace Zeeq.Core.Identity;

/// <summary>
/// Extension class that provides facilities for working with a `ClaimsPrincipal`.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    extension(ClaimsPrincipal? principal)
    {
        /// <summary>
        /// Produces a wrapper class the provides convenience properties for
        /// accessing the authenticated user's profile claims.
        /// Returns null when the principal is null or has no subject claim.
        /// </summary>
        /// <returns>
        /// An <see cref="AuthenticatedUser"/> instance if the principal is valid;
        /// otherwise, null.
        /// </returns>
        public AuthenticatedUser? AuthenticatedUser()
        {
            if (principal == null || principal.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var sub = principal.FindFirstValue(OpenIddictConstants.Claims.Subject);

            if (string.IsNullOrEmpty(sub))
            {
                return null;
            }

            var email = principal
                .FindFirstValue(OpenIddictConstants.Claims.Email)
                ?.Trim()
                .ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(email))
            {
                // Fallback to just use email.
                email = principal.FindFirstValue("email")?.Trim().ToLowerInvariant();
            }

            return new AuthenticatedUser(
                Sub: sub,
                IsAdmin: principal.IsInRole("admin"),
                Email: email,
                Name: principal.FindFirstValue(OpenIddictConstants.Claims.Name),
                Idp: principal.FindFirstValue("idp")
            );
        }
    }

    extension(ClaimsPrincipal principal)
    {
        /// <summary>
        /// Resolves the full domain view of the caller's server-issued identity claims:
        /// tenant scope, owning user, upstream IdP linkage, and session display metadata.
        /// </summary>
        /// <remarks>
        /// This is the single place that reads <see cref="AuthClaims"/> and the OpenIddict
        /// subject claim off a <see cref="ClaimsPrincipal"/>. Every org/team/owner lookup
        /// elsewhere in the codebase should build on this — or on the narrower
        /// <see cref="AsZeeqMinimalIdentity"/> — instead of re-reading claims directly, so
        /// there is exactly one mapping from claims to the application's view of the caller.
        /// </remarks>
        public ZeeqIdentity AsZeeqIdentity() =>
            new(
                OwnerUserId: principal.FindFirstValue(OpenIddictConstants.Claims.Subject)
                    ?? string.Empty,
                OrganizationId: principal.FindFirstValue(AuthClaims.OrganizationId) ?? string.Empty,
                TeamId: principal.FindFirstValue(AuthClaims.TeamId) is { Length: > 0 } teamId
                    ? teamId
                    : null,
                Provider: principal.FindFirstValue(AuthClaims.Provider),
                ProviderSubject: principal.FindFirstValue(AuthClaims.ProviderSubject),
                PartitionIdsJson: principal.FindFirstValue(AuthClaims.PartitionIds),
                OrganizationSlug: principal.FindFirstValue(AuthClaims.OrganizationSlug),
                PictureUrl: principal.FindFirstValue(AuthClaims.Picture)
            );

        /// <summary>
        /// Resolves the reduced organization/team/owner scoping needed by most tenant-scoped
        /// endpoints and MCP tools — a projection of <see cref="AsZeeqIdentity"/>.
        /// </summary>
        public ZeeqMinimalIdentity AsZeeqMinimalIdentity()
        {
            var identity = principal.AsZeeqIdentity();

            return new(identity.OrganizationId, identity.TeamId, identity.OwnerUserId);
        }
    }
}

/// <summary>
/// Represents the authenticated user's profile claims extracted from the validated
/// access token.  Use for convenience.
/// </summary>
/// <param name="Sub">The subject identifier of the user.</param>
/// <param name="IsAdmin">Indicates whether the user has administrative privileges.</param>
/// <param name="Email">The email address of the user.</param>
/// <param name="Name">The display name of the user.</param>
/// <param name="Idp">The identity provider of the user.</param>
public record AuthenticatedUser(string Sub, bool IsAdmin, string? Email, string? Name, string? Idp);

/// <summary>
/// Full domain view of the authenticated caller's server-issued identity claims.
/// </summary>
/// <remarks>
/// Deliberately lenient: missing claims resolve to an empty string (<see cref="OrganizationId"/>,
/// <see cref="OwnerUserId"/>) or <see langword="null"/> rather than throwing, so callers that
/// tolerate a missing organization or team (returning a 400/401 themselves) don't have to
/// catch an exception. Callers that require a claim to be present, such as
/// <c>AuthenticatedOwnerContext</c>, validate the relevant fields themselves.
/// </remarks>
/// <param name="OwnerUserId">Local OpenIddict subject (<c>sub</c>) claim; empty when absent.</param>
/// <param name="OrganizationId">Active organization id; empty string when absent.</param>
/// <param name="TeamId">Active team id, or <see langword="null"/> when absent.</param>
/// <param name="Provider">External IdP that authenticated the user, or <see langword="null"/>.</param>
/// <param name="ProviderSubject">External IdP subject claim, or <see langword="null"/>.</param>
/// <param name="PartitionIdsJson">
/// JSON array of selected MCP content partition ids, or <see langword="null"/> when absent.
/// </param>
/// <param name="OrganizationSlug">URL-safe slug for the active organization, or <see langword="null"/>.</param>
/// <param name="PictureUrl">Avatar URL from the external IdP profile, or <see langword="null"/>.</param>
public sealed record ZeeqIdentity(
    string OwnerUserId,
    string OrganizationId,
    string? TeamId,
    string? Provider,
    string? ProviderSubject,
    string? PartitionIdsJson,
    string? OrganizationSlug,
    string? PictureUrl
);

/// <summary>
/// Reduced organization/team/owner scoping view — the common case for tenant-scoped
/// endpoints and MCP tools that only need to know who the caller is and where they're
/// currently working, not the full session/IdP metadata carried by <see cref="ZeeqIdentity"/>.
/// </summary>
/// <param name="OrganizationId">Active organization id; empty string when absent.</param>
/// <param name="TeamId">Active team id, or <see langword="null"/> when absent.</param>
/// <param name="OwnerUserId">Local OpenIddict subject (<c>sub</c>) claim; empty when absent.</param>
public sealed record ZeeqMinimalIdentity(
    string OrganizationId,
    string? TeamId,
    string OwnerUserId
);
