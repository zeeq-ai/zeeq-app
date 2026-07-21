using System.ComponentModel.DataAnnotations;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Request body for inviting a user to the current organization.
/// </summary>
/// <param name="Email">Email address to invite.</param>
/// <param name="Role">Organization role to grant when the invitation is accepted.</param>
public sealed record CreateInvitationRequest(
    [property: Required, EmailAddress, MaxLength(320)] string Email,
    [property: Required, MaxLength(64)] string Role
);

/// <summary>
/// Pending organization invitation returned by membership endpoints.
/// </summary>
/// <param name="Id">Invitation membership row identifier.</param>
/// <param name="OrganizationId">Organization that sent the invitation.</param>
/// <param name="OrganizationName">Display name of the inviting organization.</param>
/// <param name="InvitedEmail">Email address that was invited.</param>
/// <param name="Role">Role granted when accepted.</param>
/// <param name="CreatedAtUtc">UTC timestamp when the invitation was created.</param>
/// <param name="ExpiresAtUtc">UTC timestamp when the invitation expires.</param>
public sealed record InvitationResponse(
    string Id,
    string OrganizationId,
    string? OrganizationName,
    string? InvitedEmail,
    string Role,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc
);

/// <summary>
/// Same-domain invitation details shown before a user accepts the invitation.
/// </summary>
public sealed record SameDomainInvitationDetailsResponse(
    string InvitationId,
    string OrganizationId,
    string OrganizationName,
    string? OrganizationIconUrl,
    string OwnerUserId,
    string OwnerDisplayName,
    string? OwnerEmail,
    string? OwnerPictureUrl,
    string Role
)
{
    /// <summary>
    /// Maps the persistence read model to the API response shape.
    /// </summary>
    public static SameDomainInvitationDetailsResponse From(SameDomainInvitationDetails details) =>
        new(
            details.InvitationId,
            details.OrganizationId,
            details.OrganizationName,
            details.OrganizationIconUrl,
            details.OwnerUserId,
            details.OwnerDisplayName,
            details.OwnerEmail,
            details.OwnerPictureUrl,
            details.Role
        );
}
