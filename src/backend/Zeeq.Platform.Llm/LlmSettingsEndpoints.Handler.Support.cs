using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Shared helper logic for LLM settings endpoint handlers.
/// </summary>
/// <remarks>
/// These helpers keep repeated authorization, tier validation, key-reference
/// validation, and default-key resolution out of the individual handler files
/// while preserving the endpoint-pattern file split.
/// </remarks>
internal static class LlmSettingsHandlerSupport
{
    /// <summary>
    /// Ensures the current user is an owner or admin for the organization.
    /// </summary>
    /// <remarks>
    /// Non-members are hidden with a not-found result. Active
    /// members without management rights receive a forbid result
    /// for write/test operations, while the GET route uses its own read-only
    /// notice flow.
    /// </remarks>
    public static async Task<IResult?> RequireManagerAsync(
        LlmSettingsAuthorization authorization,
        string orgId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var access = await authorization.ResolveAsync(orgId, user, cancellationToken);
        if (access is null)
        {
            return Results.NotFound();
        }

        return access.CanManage ? null : Results.Forbid();
    }

    /// <summary>
    /// Validates the minimum shape required to save the three LLM tiers.
    /// </summary>
    /// <remarks>
    /// Provider and model are required for each tier. Key IDs may be
    /// <see langword="null" /> only when a tier selects Fireworks, because that
    /// is the only provider currently allowed to use Zeeq's internal default
    /// key.
    /// </remarks>
    public static bool TryValidate(SaveLlmSettingsRequest request, out string error)
    {
        foreach (var (tierName, tier) in Enumerate(request))
        {
            if (string.IsNullOrWhiteSpace(tier.Provider))
            {
                error = $"{tierName} provider is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(tier.Model))
            {
                error = $"{tierName} model is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(tier.KeyId) && !CanUseInternalDefaultKey(tier.Provider))
            {
                error = $"{tierName} requires a managed API key for {tier.Provider.Trim()}.";
                return false;
            }

            if (
                IsAzureOpenAiProvider(tier.Provider)
                && string.IsNullOrWhiteSpace(tier.Endpoint)
            )
            {
                error = $"{tierName} requires an endpoint URL for Azure OpenAI.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Verifies that all configured tenant key references exist as active LLM API key rows.
    /// </summary>
    /// <remarks>
    /// The encrypted-value table can store multiple secret kinds. This method
    /// rejects missing, disabled, cross-organization, or non-LLM rows before the
    /// organization configuration is saved.
    /// </remarks>
    public static async Task<string?> ValidateReferencedKeysAsync(
        string orgId,
        OrganizationLlmConfiguration configuration,
        IEncryptedValueStore encryptedValues,
        CancellationToken cancellationToken
    )
    {
        foreach (var keyId in LlmSettingsContractMapping.ReferencedKeyIds(configuration))
        {
            var key = await encryptedValues.FindActiveAsync(orgId, keyId, cancellationToken);
            if (key is null || key.Kind != EncryptedValueKind.LlmApiKey)
            {
                return $"Referenced key '{keyId}' was not found.";
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the app-level default API key for a named LLM quality tier.
    /// </summary>
    /// <remarks>
    /// The <see cref="LlmModelDefaults" /> settings object already applies the
    /// Fast-key fallback for High and Max, so callers can use the tier property
    /// directly. Unknown tier names return <see langword="null" /> and are
    /// treated as an invalid key resolution by the test handler.
    /// </remarks>
    public static string? DefaultApiKeyForTier(LlmSettings settings, string tier) =>
        tier.Trim().ToLowerInvariant() switch
        {
            "fast" => settings.Models.Fast.ApiKey,
            "high" => settings.Models.High.ApiKey,
            "max" => settings.Models.Max.ApiKey,
            _ => null,
        };

    /// <inheritdoc cref="OrganizationLlmTierConfiguration.CanUseInternalDefaultKey" />
    public static bool CanUseInternalDefaultKey(string provider) =>
        OrganizationLlmTierConfiguration.CanUseInternalDefaultKey(provider);

    /// <summary>
    /// Returns <see langword="true" /> when the provider is Azure OpenAI.
    /// </summary>
    public static bool IsAzureOpenAiProvider(string provider) =>
        provider.Trim().Equals("Azure OpenAI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the OpenAI-compatible endpoint for a provider selected in the UI.
    /// </summary>
    /// <remarks>
    /// Organization LLM settings intentionally store provider/model/key metadata
    /// only. Endpoint configuration remains app-level so tenant settings cannot
    /// redirect SDK clients. When a tier request uses DeepSeek, this maps the
    /// provider back to the configured DeepSeek endpoint instead of letting the
    /// OpenAI SDK fall through to its default OpenAI endpoint.
    /// </remarks>
    public static string EndpointForProvider(LlmSettings settings, string provider, string tier)
    {
        var trimmedProvider = provider.Trim();
        var tierDefault = DefaultModelForTier(settings, tier);

        if (
            tierDefault is not null
            && trimmedProvider.Equals(tierDefault.Provider, StringComparison.OrdinalIgnoreCase)
        )
        {
            return tierDefault.Endpoint;
        }

        foreach (var modelDefault in EnumerateDefaults(settings))
        {
            if (
                trimmedProvider.Equals(modelDefault.Provider, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(modelDefault.Endpoint)
            )
            {
                return modelDefault.Endpoint;
            }
        }

        return string.Empty;
    }

    private static LlmModelDefault? DefaultModelForTier(LlmSettings settings, string tier) =>
        tier.Trim().ToLowerInvariant() switch
        {
            "fast" => settings.Models.Fast,
            "high" => settings.Models.High,
            "max" => settings.Models.Max,
            _ => null,
        };

    private static IEnumerable<LlmModelDefault> EnumerateDefaults(LlmSettings settings)
    {
        yield return settings.Models.Fast;
        yield return settings.Models.High;
        yield return settings.Models.Max;
    }

    /// <summary>
    /// Enumerates the Fast, High, and Max request tiers with display names for validation errors.
    /// </summary>
    /// <remarks>
    /// Keeping this private prevents endpoint handlers from depending on an
    /// enumeration abstraction while still avoiding three repeated validation
    /// blocks in <see cref="TryValidate" />.
    /// </remarks>
    private static IEnumerable<(string TierName, LlmTierSettingsRequest Tier)> Enumerate(
        SaveLlmSettingsRequest request
    )
    {
        yield return ("Fast", request.Fast);
        yield return ("High", request.High);
        yield return ("Max", request.Max);
    }
}
