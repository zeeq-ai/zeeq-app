namespace Zeeq.Core.Models;

/// <summary>
/// Lifecycle row for a dynamically registered OAuth 2.0 client (RFC 7591
/// Dynamic Client Registration).
/// </summary>
/// <remarks>
/// <para>
/// The row starts in <see cref="PendingLogin"/> status when an MCP client
/// posts DCR metadata. After the user authenticates via the external IdP
/// and the authorization endpoint, the row is claimed (status moves to
/// <see cref="Active"/>) and the owning user, org, team, and partition
/// scope are persisted.
/// </para>
/// <para>
/// Typical lifecycle:
/// <c>pending_login</c> → <c>active</c> → <c>revoked</c>
/// (or <c>expired</c> if never claimed before expiry).
/// </para>
/// <para>
/// <see cref="ClaimedOwnerProvider"/> / <see cref="ClaimedOwnerProviderSubject"/>
/// are audit metadata set at claim time; the active binding is through
/// <see cref="ClaimedUserId"/>.
/// </para>
/// <para>Backed by the <c>auth_dcr_client_setups</c> table.</para>
/// </remarks>
public sealed class DcrClientSetup
{
    /// <summary>DCR row created but not yet claimed by a user.</summary>
    public const string PendingLogin = "pending_login";

    /// <summary>DCR row claimed and client is operational.</summary>
    public const string Active = "active";

    /// <summary>DCR row expired before being claimed.</summary>
    public const string Expired = "expired";

    /// <summary>DCR client was revoked after being active.</summary>
    public const string Revoked = "revoked";

    /// <summary>
    /// OAuth client ID assigned by OpenIddict at DCR time.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Current lifecycle status; one of the <c>PendingLogin</c> /
    /// <c>Active</c> / <c>Expired</c> / <c>Revoked</c> constants.
    /// </summary>
    public required string Status { get; set; }

    /// <summary>
    /// Human-readable client name from the DCR registration request.
    /// </summary>
    public required string ClientName { get; init; }

    /// <summary>
    /// JSON array of allowed redirect URIs from the DCR registration
    /// request.
    /// </summary>
    public required string RedirectUrisJson { get; init; }

    /// <summary>
    /// Space-separated scope string requested by the client (e.g.
    /// <c>"mcp:tools"</c>).
    /// </summary>
    public required string RequestedScopes { get; init; }

    /// <summary>
    /// UTC timestamp when the setup row was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Absolute expiry for the pending setup; unclaimed rows past this
    /// time are dead.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>
    /// Timestamp when the user claimed this setup at the authorization
    /// endpoint.
    /// </summary>
    public DateTimeOffset? ClaimedAtUtc { get; set; }

    /// <summary>
    /// Local user ID of the claiming user; set when status moves to
    /// <see cref="Active"/>.
    /// </summary>
    public string? ClaimedUserId { get; set; }

    /// <summary>
    /// Organization selected by the claiming user.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Team selected by the claiming user.
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// JSON array of partition IDs selected by the claiming user.
    /// Defaults to <c>"[]"</c>.
    /// </summary>
    public string SelectedPartitionIdsJson { get; set; } = "[]";

    /// <summary>
    /// External IdP provider of the claiming user. Audit-only.
    /// </summary>
    public string? ClaimedOwnerProvider { get; set; }

    /// <summary>
    /// External IdP subject of the claiming user. Audit-only.
    /// </summary>
    public string? ClaimedOwnerProviderSubject { get; set; }

    /// <summary>
    /// Set when the client is revoked; <see langword="null"/> means
    /// active or not yet revoked.
    /// </summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
