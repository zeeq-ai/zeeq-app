using System.Security.Cryptography;
using System.Text;
using Zeeq.Core.Models;

namespace Zeeq.Core.Identity;

/// <summary>
/// Store boundary for organization activation-key management and exchange.
/// </summary>
public interface IOrganizationActivationKeyStore
{
    /// <summary>
    /// Creates an unused activation-key record.
    /// </summary>
    Task<OrganizationActivationKey> CreateKeyAsync(
        OrganizationActivationKey key,
        CancellationToken ct
    );

    /// <summary>
    /// Lists activation keys for system administration.
    /// </summary>
    Task<OrganizationActivationKeyPage<OrganizationActivationKeySummary>> ListKeysAsync(
        int page,
        int pageSize,
        string? query,
        OrganizationActivationKeyStatus? status,
        CancellationToken ct
    );

    /// <summary>
    /// Revokes an unused activation key.
    /// </summary>
    Task<OrganizationActivationKeySummary?> RevokeKeyAsync(string keyId, CancellationToken ct);

    /// <summary>
    /// Consumes one valid key and activates one never-activated organization.
    /// </summary>
    Task<OrganizationActivationExchangeResult> ConsumeKeyAndActivateOrganizationAsync(
        string keyHash,
        string organizationId,
        string userId,
        CancellationToken ct
    );
}

/// <summary>
/// Status filter for activation-key administration.
/// </summary>
public enum OrganizationActivationKeyStatus
{
    /// <summary>
    /// Key is valid and unused.
    /// </summary>
    Available = 1,

    /// <summary>
    /// Key was exchanged successfully.
    /// </summary>
    Activated = 2,

    /// <summary>
    /// Key was revoked before use.
    /// </summary>
    Revoked = 3,

    /// <summary>
    /// Key expired before use.
    /// </summary>
    Expired = 4,
}

/// <summary>
/// Paged activation-key result.
/// </summary>
public sealed record OrganizationActivationKeyPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount
);

/// <summary>
/// Activation-key row returned to system administrators.
/// </summary>
public sealed record OrganizationActivationKeySummary(
    string Id,
    string? Note,
    string CreatedByUserId,
    string CreatedByDisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ActivatedAtUtc,
    string? ActivatedOrganizationId,
    string? ActivatedByUserId,
    DateTimeOffset? DisabledAtUtc,
    OrganizationActivationKeyStatus Status
);

/// <summary>
/// Result of consuming an activation key.
/// </summary>
public enum OrganizationActivationExchangeResult
{
    /// <summary>
    /// Organization was activated.
    /// </summary>
    Activated,

    /// <summary>
    /// Key does not exist or is no longer usable.
    /// </summary>
    InvalidKey,

    /// <summary>
    /// Organization is missing, already activated, disabled, or not owned by the token user.
    /// </summary>
    InvalidOrganization,
}

/// <summary>
/// Cache keys used by active-organization endpoint filters.
/// </summary>
public static class OrganizationActivationCacheKeys
{
    private const string CacheKeyPrefix = "identity:organization-activation-state:";

    /// <summary>
    /// Gets the cache key for a single organization's activation state.
    /// </summary>
    public static string ForOrganization(string organizationId) => CacheKeyPrefix + organizationId;
}

/// <summary>
/// Generates and hashes activation-key material.
/// </summary>
public static class OrganizationActivationKeyMaterial
{
    private const int KeyByteLength = 32;
    private const int KeyHexLength = KeyByteLength * 2;

    /// <summary>
    /// Generates a 32-byte random activation key encoded as lower-case hex.
    /// </summary>
    public static string GenerateKey()
    {
        Span<byte> bytes = stackalloc byte[KeyByteLength];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Validates the user-visible activation-key format.
    /// </summary>
    public static bool IsValidKeyFormat(string? key) =>
        Normalize(key) is { Length: KeyHexLength } normalized
        && normalized.All(Uri.IsHexDigit);

    /// <summary>
    /// Computes the database hash for a user-visible activation key.
    /// </summary>
    public static string ComputeHash(string key)
    {
        var normalized = Normalize(key);
        if (!IsValidKeyFormat(normalized))
        {
            throw new ArgumentException("Activation key must be 64 hex characters.", nameof(key));
        }

        var bytes = Encoding.UTF8.GetBytes(normalized);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim().ToLowerInvariant();
}
