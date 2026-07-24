using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Abstractions;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Resolves the authenticated caller's profile, org list, and current
/// org role from the membership store at request time.
/// </summary>
/// <remarks>
/// <para>
/// Role and org list are resolved fresh from
/// <see cref="IZeeqMembershipStore"/> rather than read from the cookie/JWT
/// claims. This means role changes take effect immediately without requiring
/// token re-issue.
/// </para>
/// <para>
/// Three queries:
/// <list type="number">
///   <item><c>ListActiveMembershipsForUserAsync</c> — user's orgs + roles</item>
///   <item><c>ListPendingInvitationsForEmailAsync</c> — pending invites for the user's email</item>
///   <item><c>FindOrganizationsByIdsAsync</c> — slug, display name, and icon for each org</item>
/// </list>
/// Pending invitations are merged into <c>orgs[]</c> with <c>status: "pending"</c>
/// so the UI can render a single org list with badges.
/// </para>
/// </remarks>
public sealed class GetMeHandler(
    IZeeqMembershipStore membershipStore,
    IZeeqIdentityStore identityStore,
    SystemAdminEvaluator systemAdminEvaluator
) : IEndpointHandler
{
    internal async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> HandleAsync(
        ClaimsPrincipal user,
        CancellationToken ct
    )
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return TypedResults.Unauthorized();
        }

        var identity = user.AsZeeqIdentity();
        var userId = identity.OwnerUserId;
        var orgId = identity.OrganizationId;
        var email =
            user.FindFirstValue(OpenIddictConstants.Claims.Email)
            ?? user.FindFirstValue(ClaimTypes.Email);

        // Resolve role + active org list from store at request time (not from claims)
        var memberships = await membershipStore.ListActiveMembershipsForUserAsync(userId!, ct);
        var currentMembership = memberships.FirstOrDefault(m => m.OrganizationId == orgId);

        // Resolve pending invitations by email (UserId is null on these rows)
        var pending = await membershipStore.ListPendingInvitationsForEmailAsync(email!, ct);

        // Collect org IDs from both active and pending, then batch-lookup display info
        var orgIds = memberships
            .Select(m => m.OrganizationId)
            .Concat(pending.Select(p => p.OrganizationId))
            .Distinct()
            .ToArray();

        var orgs = await membershipStore.FindOrganizationsByIdsAsync(orgIds, ct);
        var orgMap = orgs.ToDictionary(o => o.Id);

        var aliases =
            !string.IsNullOrWhiteSpace(orgId) && !string.IsNullOrWhiteSpace(userId)
                ? await identityStore.ListUserAliasesAsync(orgId, userId, ct)
                : [];

        // Build combined org list: active first, then pending
        var orgSummaries = new List<OrgSummary>(memberships.Count + pending.Count);

        foreach (var m in memberships)
        {
            var org = orgMap.GetValueOrDefault(m.OrganizationId);

            orgSummaries.Add(
                new OrgSummary(
                    m.OrganizationId,
                    null,
                    org?.Slug,
                    org?.DisplayName ?? "",
                    org?.IconUrl,
                    m.Role,
                    m.IsDefault,
                    Status: MembershipStatus.Active,
                    ActivatedAtUtc: org?.ActivatedAtUtc
                )
            );
        }

        foreach (var p in pending)
        {
            var org = orgMap.GetValueOrDefault(p.OrganizationId);

            orgSummaries.Add(
                new OrgSummary(
                    p.OrganizationId,
                    p.Id,
                    org?.Slug,
                    org?.DisplayName ?? "",
                    org?.IconUrl,
                    p.Role,
                    IsDefault: false,
                    Status: MembershipStatus.Pending,
                    ActivatedAtUtc: org?.ActivatedAtUtc
                )
            );
        }

        return TypedResults.Ok(
            new MeResponse(
                UserId: userId,
                Subject: userId,
                OrganizationId: orgId,
                TeamId: identity.TeamId,
                Provider: identity.Provider,
                ProviderSubject: identity.ProviderSubject,
                Name: user.FindFirstValue(OpenIddictConstants.Claims.Name) ?? user.Identity.Name,
                Email: email,
                PictureUrl: identity.PictureUrl,
                OrganizationRole: currentMembership?.Role,
                OrganizationSlug: identity.OrganizationSlug,
                Organizations: orgSummaries,
                Aliases: [.. aliases.Select(UserAliasEndpointMapping.ToDto)],
                IsSystemAdmin: systemAdminEvaluator.IsSystemAdmin(user)
            )
        );
    }
}
