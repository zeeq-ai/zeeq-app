namespace Zeeq.Core.Models;

/// <summary>
/// Local ownership record for an OAuth 2.0 client credential issued by
/// OpenIddict.
/// </summary>
/// <remarks>
/// Each row binds an OAuth <see cref="ClientId"/> to a local
/// <see cref="User"/>, <see cref="Organization"/>, and <see cref="Team"/>.
/// The <see cref="OwnerProvider"/> / <see cref="OwnerProviderSubject"/>
/// pair records which external identity was used when the credential was
/// created (audit metadata, not an active binding).
/// <see cref="SelectedPartitionIdsJson"/> scopes the credential to specific
/// content partitions.
/// Backed by the <c>auth_client_credentials</c> table.
/// </remarks>
public sealed class ClientCredential
{
    /// <summary>
    /// OAuth client ID assigned by OpenIddict.
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Local user who owns this credential.
    /// </summary>
    public required string OwnerUserId { get; init; }

    /// <summary>
    /// Organization the credential is scoped to.
    /// </summary>
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Team the credential is scoped to.
    /// </summary>
    public required string TeamId { get; init; }

    /// <summary>
    /// External IdP provider used when creating the credential (e.g.
    /// <c>"google"</c>). Audit-only; the active binding is via
    /// <see cref="OwnerUserId"/>.
    /// </summary>
    public required string OwnerProvider { get; init; }

    /// <summary>
    /// External IdP subject used when creating the credential. Audit-only.
    /// </summary>
    public required string OwnerProviderSubject { get; init; }

    /// <summary>
    /// Human-readable label shown in credential lists.
    /// </summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// OAuth client secret (generated once, never returned from list
    /// endpoints or logged).
    /// </summary>
    public required string ClientSecret { get; init; }

    /// <summary>
    /// JSON array of partition IDs this credential can access.
    /// </summary>
    public required string SelectedPartitionIdsJson { get; init; }

    /// <summary>
    /// UTC timestamp when the credential was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// Set when the credential is revoked; <see langword="null"/> means
    /// active.
    /// </summary>
    public DateTimeOffset? RevokedAtUtc { get; set; }
}
