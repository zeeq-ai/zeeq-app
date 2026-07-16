namespace Zeeq.Core.Models;

/// <summary>
/// Organization-scoped encrypted secret value.
/// </summary>
/// <remarks>
/// The encrypted payload is opaque to EF and application services. The
/// provider name records which encryption adapter encrypted the bytes and
/// therefore which adapter must be used to decrypt them later.
/// </remarks>
public sealed class EncryptedValue : MutableDomainEntityBase, IOrganizationScopedEntity
{
    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <summary>
    /// Logical kind of secret material stored in this row.
    /// </summary>
    public required EncryptedValueKind Kind { get; init; }

    /// <summary>
    /// Encryption adapter that produced the ciphertext.
    /// </summary>
    public required string EncryptionProvider { get; set; }

    /// <summary>
    /// User-provided display name for the encrypted value.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Encrypted secret bytes.
    /// </summary>
    public required byte[] Ciphertext { get; set; }
}

/// <summary>
/// Logical encrypted value categories.
/// </summary>
public enum EncryptedValueKind
{
    /// <summary>
    /// Generic secret string value, such as a password or token.
    /// </summary>
    SecretString = 0,

    /// <summary>
    /// API key used by an organization-owned LLM provider configuration.
    /// </summary>
    LlmApiKey = 1,
}
