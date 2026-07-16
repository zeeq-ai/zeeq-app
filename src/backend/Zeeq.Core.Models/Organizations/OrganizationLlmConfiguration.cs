namespace Zeeq.Core.Models;

/// <summary>
/// Organization-level LLM model configuration for Zeeq's quality tiers.
/// </summary>
/// <remarks>
/// This is persisted as one typed JSON document on the organization row. The
/// provider values remain strings so core models stay independent of the shared
/// LLM service package.
/// </remarks>
public sealed record OrganizationLlmConfiguration
{
    /// <summary>
    /// Default Fireworks-backed configuration used when an organization has not customized LLM settings.
    /// </summary>
    public static OrganizationLlmConfiguration Default { get; } =
        new()
        {
            Fast = new OrganizationLlmTierConfiguration
            {
                Provider = "Fireworks",
                Model = "accounts/fireworks/models/deepseek-v4-flash",
            },
            High = new OrganizationLlmTierConfiguration
            {
                Provider = "Fireworks",
                Model = "accounts/fireworks/models/deepseek-v4-pro",
            },
            Max = new OrganizationLlmTierConfiguration
            {
                Provider = "Fireworks",
                Model = "accounts/fireworks/models/glm-5p2",
            },
        };

    /// <summary>
    /// The pre-Fireworks migration-seeded default. Organizations still carrying
    /// this exact DeepSeek-direct configuration never customized their settings,
    /// so readers treat it as "no saved configuration" and fall back to
    /// <see cref="Default" /> (Fireworks GLM 5.2) rather than failing the
    /// Fireworks-only internal-key policy.
    /// </summary>
    public static OrganizationLlmConfiguration LegacyDeepSeekDefault { get; } =
        new()
        {
            Fast = new OrganizationLlmTierConfiguration
            {
                Provider = "DeepSeek",
                Model = "deepseek-v4-flash",
            },
            High = new OrganizationLlmTierConfiguration
            {
                Provider = "DeepSeek",
                Model = "deepseek-v4-pro",
            },
            Max = new OrganizationLlmTierConfiguration
            {
                Provider = "DeepSeek",
                Model = "deepseek-v4-pro",
            },
        };

    /// <summary>
    /// Returns <see langword="true" /> when this configuration is exactly the
    /// pre-Fireworks migration-seeded DeepSeek-direct default and should be
    /// treated as "no saved configuration" by readers.
    /// </summary>
    public bool IsLegacyDeepSeekDefault() => this == LegacyDeepSeekDefault;

    /// <summary>
    /// Fast model tier, optimized for speed and cost.
    /// </summary>
    public OrganizationLlmTierConfiguration Fast { get; init; } = new();

    /// <summary>
    /// Higher-quality model tier.
    /// </summary>
    public OrganizationLlmTierConfiguration High { get; init; } = new();

    /// <summary>
    /// Maximum-quality model tier.
    /// </summary>
    public OrganizationLlmTierConfiguration Max { get; init; } = new();
}

/// <summary>
/// Provider/model/key selection for one organization LLM tier.
/// </summary>
public sealed record OrganizationLlmTierConfiguration
{
    /// <summary>
    /// Provider name, such as Fireworks, OpenAI, or Anthropic.
    /// </summary>
    public string Provider { get; init { field = value.Trim(); } } = "Fireworks";

    /// <summary>
    /// Provider model identifier.
    /// </summary>
    public string Model { get; init { field = value.Trim(); } } = string.Empty;

    /// <summary>
    /// Tenant-owned encrypted value ID; <see langword="null" /> uses Zeeq's internal default key only for Fireworks tiers.
    /// </summary>
    public string? KeyId { get; init; }

    /// <summary>
    /// Optional provider endpoint URL. Required for Azure OpenAI (e.g. <c>https://my-instance.openai.azure.com/</c>);
    /// left empty for providers whose endpoints are configured at the application level.
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Returns whether a provider may use Zeeq's internal default key instead of a tenant-managed key.
    /// </summary>
    /// <remarks>
    /// Only Fireworks is eligible because Zeeq's internal app-level key is a Fireworks key.
    /// OpenAI, Anthropic, and other providers must supply a tenant-managed encrypted key.
    /// </remarks>
    public static bool CanUseInternalDefaultKey(string provider) =>
        provider.Equals("Fireworks", StringComparison.OrdinalIgnoreCase);
}
