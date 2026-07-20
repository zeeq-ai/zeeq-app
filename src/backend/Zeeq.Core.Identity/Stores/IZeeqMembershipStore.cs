using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Read-model projection for listing org members with user display info.
/// </summary>
/// <param name="UserId">Local user ID for the member row.</param>
/// <param name="DisplayName">Current display name from the user profile.</param>
/// <param name="Email">Current email address from the user profile, if known.</param>
/// <param name="PictureUrl">Current profile image URL from the user profile, if known.</param>
/// <param name="Role">Organization role assigned by the active membership row.</param>
/// <param name="JoinedAtUtc">Timestamp when the active membership row was created.</param>
public sealed record OrganizationMember(
    string UserId,
    string DisplayName,
    string? Email,
    string? PictureUrl,
    string Role,
    DateTimeOffset JoinedAtUtc
);

/// <summary>
/// Persistence contract for organization membership: org CRUD, member
/// listing, role changes, invitations, and default-org management.
/// </summary>
/// <remarks>
/// This store is the identity-layer boundary for organization membership. It is
/// used by browser API handlers, the <c>/me</c> response, and membership
/// enrichment middleware to resolve organization role data from server-side
/// state rather than from long-lived cookie or bearer-token claims.
/// <para>
/// Active memberships are rows where <see cref="OrganizationMembership.Status"/>
/// is <see cref="MembershipStatus.Active"/> and
/// <see cref="ICanBeDisabled.DisabledAtUtc"/> is <see langword="null"/>.
/// Pending invitations are represented by the same table with
/// <see cref="MembershipStatus.Pending"/> and a <see langword="null"/> user ID.
/// </para>
/// </remarks>
public interface IZeeqMembershipStore
{
    // ── Organizations ──────────────────────────────────────

    /// <summary>
    /// Looks up an organization by its opaque ID. Used by the
    /// membership enrichment middleware to resolve slug + display name.
    /// </summary>
    /// <param name="orgId">Opaque organization ID, e.g. <c>org_...</c>.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>The organization if it exists; <see langword="null"/> otherwise.</returns>
    Task<Organization?> FindOrganizationByIdAsync(string orgId, CancellationToken ct);

    /// <summary>
    /// Batch lookup for building the <c>orgs[]</c> array in the /me
    /// response without N+1 queries.
    /// </summary>
    /// <param name="orgIds">Organization IDs to resolve. Empty arrays should return an empty result.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>Organizations matching the supplied IDs. Missing IDs are omitted.</returns>
    Task<IReadOnlyList<Organization>> FindOrganizationsByIdsAsync(
        string[] orgIds,
        CancellationToken ct
    );

    /// <summary>
    /// Resolves the activation state used by endpoint filters.
    /// </summary>
    /// <param name="orgId">Organization ID to check.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>
    /// A narrow activation projection when the organization exists; otherwise
    /// <see langword="null" />.
    /// </returns>
    Task<OrganizationActivationState?> FindOrganizationActivationStateAsync(
        string orgId,
        CancellationToken ct
    );

    /// <summary>
    /// Looks up an organization by its URL-safe slug. Used for
    /// org-scoped UI routing (<c>/o/{slug}/home</c>).
    /// </summary>
    /// <param name="slug">URL-safe organization slug.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>The organization with the given slug; <see langword="null"/> if no match exists.</returns>
    Task<Organization?> FindOrganizationBySlugAsync(string slug, CancellationToken ct);

    /// <summary>
    /// Checks whether a slug is available for use. Excludes the given
    /// org ID so an org can keep its own slug on edit.
    /// </summary>
    /// <param name="slug">Candidate URL-safe slug to check.</param>
    /// <param name="excludeOrgId">Optional organization ID to ignore during an edit flow.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns><see langword="true"/> when no other organization owns the slug.</returns>
    Task<bool> IsSlugAvailableAsync(string slug, string? excludeOrgId, CancellationToken ct);

    /// <summary>
    /// Persists updates to an existing organization row.
    /// </summary>
    /// <param name="org">Organization entity with updated display name, slug, or icon data.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <remarks>
    /// Callers are responsible for validating slug format, slug availability,
    /// icon size, and caller authorization before invoking this method.
    /// </remarks>
    Task UpdateOrganizationAsync(Organization org, CancellationToken ct);

    /// <summary>
    /// Persists only the same-domain onboarding organization settings.
    /// </summary>
    /// <param name="organization">Organization entity carrying the new same-domain settings.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>
    /// <see langword="true"/> when the settings were saved; <see langword="false"/>
    /// when another enabled organization already owns the requested domain.
    /// </returns>
    Task<bool> UpdateOrganizationSameDomainOnboardingAsync(
        Organization organization,
        CancellationToken ct
    );

    /// <summary>
    /// Resolves a user's current primary email address by local user ID.
    /// </summary>
    /// <param name="userId">Local user ID to resolve.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>The user's email address, or <see langword="null"/> when missing.</returns>
    Task<string?> FindUserEmailByIdAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Batch resolves current primary email addresses by local user ID.
    /// </summary>
    /// <param name="userIds">Local user IDs to resolve.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>Email addresses keyed by user ID. Missing users are omitted.</returns>
    Task<IReadOnlyDictionary<string, string?>> FindUserEmailsByIdsAsync(
        string[] userIds,
        CancellationToken ct
    );

    /// <summary>
    /// Checks whether a normalized same-domain onboarding domain can be claimed.
    /// </summary>
    /// <param name="domain">Normalized registrable domain, e.g. <c>example.com</c>.</param>
    /// <param name="excludeOrgId">Organization ID to ignore during updates.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns><see langword="true"/> when no other enabled organization owns the domain.</returns>
    Task<bool> IsAutoInviteSameDomainAvailableAsync(
        string domain,
        string excludeOrgId,
        CancellationToken ct
    );

    /// <summary>
    /// Finds active organizations that currently claim any of the supplied same-domain onboarding domains.
    /// </summary>
    /// <param name="domains">Normalized registrable domains to check.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>Claiming organization ID keyed by normalized domain. Unclaimed domains are omitted.</returns>
    Task<IReadOnlyDictionary<string, string>> FindAutoInviteSameDomainClaimsAsync(
        string[] domains,
        CancellationToken ct
    );

    /// <summary>
    /// Counts organizations originally created by the supplied user.
    /// </summary>
    /// <param name="userId">Local user ID to count created organizations for.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>Total number of organization rows created by the user.</returns>
    Task<int> CountOrganizationsCreatedByUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Creates an organization with its root team and owner memberships atomically.
    /// </summary>
    /// <param name="organization">Organization row to insert.</param>
    /// <param name="rootTeam">Root team row to insert.</param>
    /// <param name="ownerMembership">Owner organization membership row to insert.</param>
    /// <param name="rootTeamMembership">Root team membership row to insert.</param>
    /// <param name="maxCreatedOrganizations">Maximum organizations the creator may own.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>The inserted organization row, or <see langword="null"/> if the creator is at the limit.</returns>
    Task<Organization?> CreateOrganizationAsync(
        Organization organization,
        Team rootTeam,
        OrganizationMembership ownerMembership,
        TeamMembership rootTeamMembership,
        int maxCreatedOrganizations,
        CancellationToken ct
    );

    // ── Memberships (Status = MembershipStatus.Active) ────

    /// <summary>
    /// Returns all active memberships for a user. This is the single
    /// query that powers both the /me handler (org list + current role)
    /// and the enrichment middleware (role claim injection).
    /// </summary>
    /// <param name="userId">Local user ID whose active memberships should be returned.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>
    /// Active, non-disabled membership rows for non-disabled organizations. Pending,
    /// declined, disabled, and different-user rows are omitted.
    /// </returns>
    Task<IReadOnlyList<OrganizationMembership>> ListActiveMembershipsForUserAsync(
        string userId,
        CancellationToken ct
    );

    /// <summary>
    /// Read-model projection for the member list UI (Settings → Members).
    /// Joins users for display info; excludes pending invitations.
    /// </summary>
    /// <param name="orgId">Organization whose active members should be listed.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>Active organization members with current user display information.</returns>
    Task<IReadOnlyList<OrganizationMember>> ListMembersForOrganizationAsync(
        string orgId,
        CancellationToken ct
    );

    /// <summary>
    /// Sets the user's default organization, atomically unsetting any
    /// existing default. Wrapped in a transaction to avoid duplicate defaults.
    /// </summary>
    /// <param name="userId">Local user ID whose default organization should change.</param>
    /// <param name="orgId">Target organization ID. The user must have an active membership in this org.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <remarks>
    /// Implementations must fail if the target active membership does not exist.
    /// This prevents clearing the previous default and silently leaving the user
    /// without a valid default organization.
    /// </remarks>
    Task SetDefaultOrganizationAsync(string userId, string orgId, CancellationToken ct);

    /// <summary>
    /// Changes a member's role. Caller must validate the current user
    /// has owner/admin rights before invoking.
    /// </summary>
    /// <param name="orgId">Organization containing the member row.</param>
    /// <param name="userId">User whose active membership role should change.</param>
    /// <param name="newRole">Validated role value, e.g. <c>owner</c>, <c>admin</c>, or <c>member</c>.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <remarks>
    /// This method does not enforce role names, last-owner rules, or caller
    /// authorization. Those checks belong in the API handler layer.
    /// </remarks>
    Task UpdateMemberRoleAsync(string orgId, string userId, string newRole, CancellationToken ct);

    /// <summary>
    /// Disables a membership (Status → MembershipStatus.Disabled). Does not delete
    /// the row — preserves audit history.
    /// </summary>
    /// <param name="orgId">Organization containing the member row.</param>
    /// <param name="userId">User whose active membership should be disabled.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <remarks>
    /// Caller must enforce owner/admin authorization and any last-owner guard
    /// before invoking this method.
    /// </remarks>
    Task RemoveMemberAsync(string orgId, string userId, CancellationToken ct);

    /// <summary>
    /// Self-service leave: disables the calling user's own membership.
    /// Must enforce last-owner guard at the caller level.
    /// </summary>
    /// <param name="orgId">Organization the caller is leaving.</param>
    /// <param name="userId">Local user ID for the caller.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <remarks>
    /// This has the same persistence semantics as
    /// <see cref="RemoveMemberAsync"/>. It is separated in the contract so the
    /// API layer can keep self-service leave flows distinct from admin removal.
    /// </remarks>
    Task LeaveOrganizationAsync(string orgId, string userId, CancellationToken ct);

    /// <summary>
    /// Finds the root team ID for a user's active membership in an organization.
    /// </summary>
    /// <param name="orgId">Organization whose root team should be resolved.</param>
    /// <param name="userId">Local user ID that must belong to the root team.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>The root team ID when the user belongs to it; otherwise <see langword="null"/>.</returns>
    Task<string?> FindRootTeamIdForMemberAsync(string orgId, string userId, CancellationToken ct);

    // ── Invitations (Status = MembershipStatus.Pending) ───

    /// <summary>
    /// Creates a pending membership row (UserId = null, Status = MembershipStatus.Pending).
    /// </summary>
    /// <param name="invitation">
    /// Pending membership row to insert. The row must include organization ID,
    /// invited email, role, creator, creation timestamp, and expiration timestamp.
    /// </param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>The inserted invitation row.</returns>
    /// <remarks>
    /// Callers must validate email format, assignable role, duplicate pending
    /// invitations, and caller authorization before invoking this method.
    /// </remarks>
    Task<OrganizationMembership> CreateInvitationAsync(
        OrganizationMembership invitation,
        CancellationToken ct
    );

    /// <summary>
    /// Lists pending invitations for an email address.
    /// </summary>
    /// <param name="email">Email address from the authenticated user's profile or invite form.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>
    /// Pending, non-disabled, non-expired invitations for the supplied email address.
    /// </returns>
    Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForEmailAsync(
        string email,
        CancellationToken ct
    );

    /// <summary>
    /// Lists pending invitations sent for an organization.
    /// </summary>
    /// <param name="orgId">Organization whose outgoing invitations should be returned.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>Pending, non-disabled, non-expired invitations for the supplied organization.</returns>
    Task<IReadOnlyList<OrganizationMembership>> ListPendingInvitationsForOrganizationAsync(
        string orgId,
        CancellationToken ct
    );

    /// <summary>
    /// Accepts an invitation: sets UserId, Status → MembershipStatus.Active,
    /// clears ExpiresAtUtc, and adds the user to the organization's root team.
    /// Returns <c>true</c> if a row was updated.
    /// </summary>
    /// <param name="membershipId">Invitation membership row ID.</param>
    /// <param name="userId">Local user accepting the invitation.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>
    /// <see langword="true"/> if the invitation was transitioned to active;
    /// <see langword="false"/> if it was missing, not pending, or could not be accepted.
    /// </returns>
    /// <remarks>
    /// Callers must verify that the invitation email belongs to the authenticated
    /// user before invoking this method. Implementations must avoid violating the
    /// unique active membership constraint when the user is already a member.
    /// Implementations should create the root team membership in the same
    /// transaction so the accepted organization can be switched to immediately.
    /// </remarks>
    Task<bool> AcceptInvitationAsync(string membershipId, string userId, CancellationToken ct);

    /// <summary>
    /// Declines an invitation: Status → MembershipStatus.Declined, sets DisabledAtUtc.
    /// Returns <c>true</c> if a row was updated.
    /// </summary>
    /// <param name="membershipId">Invitation membership row ID.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>
    /// <see langword="true"/> if a pending invitation was declined;
    /// <see langword="false"/> if no pending row exists.
    /// </returns>
    /// <remarks>
    /// Callers must verify that the invitation email belongs to the authenticated
    /// user before invoking this method.
    /// </remarks>
    Task<bool> DeclineInvitationAsync(string membershipId, CancellationToken ct);

    /// <summary>
    /// Cancels an organization-sent invitation.
    /// </summary>
    /// <param name="orgId">Organization that owns the pending invitation.</param>
    /// <param name="membershipId">Invitation membership row ID.</param>
    /// <param name="ct">Cancellation token for the database operation.</param>
    /// <returns>
    /// <see langword="true"/> if a pending invitation was canceled;
    /// <see langword="false"/> if no matching pending row exists.
    /// </returns>
    /// <remarks>
    /// Caller must verify owner/admin authorization before invoking this method.
    /// Canceled invitations are marked declined/disabled instead of physically
    /// deleted so the membership table preserves invitation history.
    /// </remarks>
    Task<bool> CancelInvitationAsync(string orgId, string membershipId, CancellationToken ct);
}
