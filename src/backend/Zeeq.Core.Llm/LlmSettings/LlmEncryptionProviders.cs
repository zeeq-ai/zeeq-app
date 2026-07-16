namespace Zeeq.Core.Llm;

/// <summary>
/// Stable encryption provider names for tenant key rows.
/// </summary>
public static class LlmEncryptionProviders
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
