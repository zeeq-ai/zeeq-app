namespace Zeeq.Core.Security;

/// <summary>
/// Provider-neutral configuration for organization-owned encrypted-value operations.
/// </summary>
/// <remarks>
/// Extracted from <c>Zeeq.Core.Llm.LlmSettings</c> (zeeq-ai/zeeq-app#192) so encrypted-value
/// encryption is no longer configured through an LLM-specific settings type. The runtime host
/// still binds the underlying values from the existing <c>AppSettings:Llm:*</c> configuration
/// keys and maps them onto this type at composition time — the config surface is unchanged.
/// </remarks>
public sealed record SecuritySettings
{
    /// <summary>
    /// Active encryption provider used for newly encrypted rows. One of
    /// <see cref="DataEncryptionProviders.DataProtection"/> or
    /// <see cref="DataEncryptionProviders.CloudKms"/>.
    /// </summary>
    public required string EncryptionProvider { get; init; }

    /// <summary>
    /// Local Data Protection key-ring path used by the <see cref="DataEncryptionProviders.DataProtection"/> provider.
    /// </summary>
    public required string DataProtectionKeyRingPath { get; init; }

    /// <summary>
    /// Google Cloud KMS crypto key resource name used by the <see cref="DataEncryptionProviders.CloudKms"/> provider.
    /// </summary>
    public required string GoogleKmsKeyName { get; init; }
}
