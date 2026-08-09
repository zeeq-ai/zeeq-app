namespace Zeeq.Core.Security;

/// <summary>
/// Stable encryption provider names persisted on <see cref="Zeeq.Core.Models.EncryptedValue"/> rows.
/// </summary>
/// <remarks>
/// Renamed from <c>Zeeq.Core.Llm.LlmEncryptionProviders</c> (zeeq-ai/zeeq-app#192) — the string
/// values are unchanged so existing persisted rows keep resolving to the same provider.
/// </remarks>
public static class DataEncryptionProviders
{
    /// <summary>
    /// ASP.NET Core Data Protection provider for local development.
    /// </summary>
    public const string DataProtection = "data-protection";

    /// <summary>
    /// Google Cloud KMS provider for production.
    /// </summary>
    public const string CloudKms = "cloud-kms";
}
