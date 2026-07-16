namespace Zeeq.Core.Models;

/// <summary>
/// Local metadata for a user-owned long-lived JWE bearer token issued by
/// OpenIddict.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ClientCredential"/> (which represents an OAuth client
/// that exchanges codes for tokens), <see cref="UserToken"/> represents a
/// pre-issued long-lived token that the user can copy and use directly.
/// </para>
/// <para>
/// The token payload carries the owner's user/org/team/partition context.
/// Validation updates <see cref="LastUsedAtUtc"/> for audit.
/// </para>
/// <para>Backed by the <c>auth_user_tokens</c> table.</para>
/// </remarks>
public sealed class UserToken
{
    /// <summary>
    /// Opaque token ID (stored in the JWE <c>jti</c> claim).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Local user who owns this token.
    /// </summary>
    public required string OwnerUserId { get; init; }

    /// <summary>
    /// Organization the token is scoped to.
    /// </summary>
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Team the token is scoped to.
    /// </summary>
    public required string TeamId { get; init; }

    /// <summary>
    /// External IdP provider used when creating the token (e.g.
    /// <c>"google"</c>). Audit-only.
    /// </summary>
    public required string OwnerProvider { get; init; }

    /// <summary>
    /// External IdP subject used when creating the token. Audit-only.
    /// </summary>
    public required string OwnerProviderSubject { get; init; }

    /// <summary>
    /// Human-readable label shown in token lists.
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// JSON array of partition IDs this token can access.
    /// </summary>
    public required string SelectedPartitionIdsJson { get; init; }

    /// <summary>
    /// UTC timestamp when the token metadata was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Absolute expiry of the token.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>
    /// Set when the token is revoked; <see langword="null"/> means active.
    /// </summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }

    /// <summary>
    /// Updated on each successful bearer-token validation for audit
    /// visibility.
    /// </summary>
    public DateTimeOffset? LastUsedAtUtc { get; set; }
}
