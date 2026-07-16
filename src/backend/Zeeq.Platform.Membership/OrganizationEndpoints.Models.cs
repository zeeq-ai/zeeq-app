using System.ComponentModel.DataAnnotations;

namespace Zeeq.Platform.Membership;

/// <summary>
/// Result of checking whether an organization slug can be used.
/// </summary>
/// <param name="Slug">Slug value that was checked.</param>
/// <param name="Available">Whether the slug is available.</param>
public sealed record SlugCheckResponse(string Slug, bool Available);

// NOTE: Slug is intentionally retained on create requests even though the
// server currently generates it. The UI hides slug editing today, but product
// may add a caller-provided create slug later.
/// <summary>
/// Request body for creating an organization.
/// </summary>
/// <param name="DisplayName">Human-readable organization name.</param>
/// <param name="Slug">Optional caller-provided slug.</param>
/// <param name="IconUrl">Optional organization icon data URL.</param>
public sealed record CreateOrganizationRequest(
    [property: Required, MaxLength(200)] string DisplayName,
    [property: MaxLength(128), RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")] string? Slug,
    [property: MaxLength(87_380)] string? IconUrl
);

/// <summary>
/// Request body for updating organization display details.
/// </summary>
/// <param name="DisplayName">Optional replacement display name.</param>
/// <param name="Slug">Optional replacement slug.</param>
/// <param name="IconUrl">Optional replacement icon data URL.</param>
public sealed record UpdateOrganizationRequest(
    [property: MaxLength(200)] string? DisplayName,
    [property: MaxLength(128), RegularExpression(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")] string? Slug,
    [property: MaxLength(87_380)] string? IconUrl
);

/// <summary>
/// Organization details returned by membership endpoints.
/// </summary>
/// <param name="Id">Stable organization identifier.</param>
/// <param name="Slug">Organization slug.</param>
/// <param name="DisplayName">Human-readable organization name.</param>
/// <param name="IconUrl">Optional organization icon data URL.</param>
/// <param name="Role">Current user's role in the organization, when available.</param>
/// <param name="CreatedAtUtc">UTC timestamp when the organization was created.</param>
/// <param name="ActivatedAtUtc">UTC timestamp when the organization became active.</param>
public sealed record OrganizationResponse(
    string Id,
    string? Slug,
    string DisplayName,
    string? IconUrl,
    string? Role,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ActivatedAtUtc
);
