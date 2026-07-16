using System.ComponentModel.DataAnnotations;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Organization member displayed in member lists.
/// </summary>
/// <param name="UserId">Stable local user identifier.</param>
/// <param name="DisplayName">Member display name.</param>
/// <param name="Email">Member email address, when available.</param>
/// <param name="PictureUrl">Member avatar URL, when available.</param>
/// <param name="Role">Member organization role.</param>
/// <param name="JoinedAtUtc">UTC timestamp when the member joined.</param>
public sealed record MemberResponse(
    string UserId,
    string DisplayName,
    string? Email,
    string? PictureUrl,
    string Role,
    DateTimeOffset JoinedAtUtc
);

/// <summary>
/// Request body for changing a member's organization role.
/// </summary>
/// <param name="Role">Replacement organization role.</param>
public sealed record ChangeMemberRoleRequest([property: Required, MaxLength(64)] string Role);
