using System.ComponentModel.DataAnnotations;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Llm;

/// <summary>
/// Response returned by the organization LLM settings route.
/// </summary>
public sealed record LlmSettingsViewResponse(
    bool CanManage,
    string? Notice,
    LlmSettingsConfigurationResponse? Configuration,
    IReadOnlyList<LlmApiKeyResponse> Keys
);

/// <summary>
/// Organization LLM tier configuration returned to the settings UI.
/// </summary>
public sealed record LlmSettingsConfigurationResponse(
    LlmTierSettingsResponse Fast,
    LlmTierSettingsResponse High,
    LlmTierSettingsResponse Max
);

/// <summary>
/// Provider/model/key selection for one tier.
/// </summary>
public sealed record LlmTierSettingsResponse(string Provider, string Model, string? KeyId, string? Endpoint);

/// <summary>
/// Request body for saving organization LLM tier settings.
/// </summary>
public sealed record SaveLlmSettingsRequest(
    [property: Required] LlmTierSettingsRequest Fast,
    [property: Required] LlmTierSettingsRequest High,
    [property: Required] LlmTierSettingsRequest Max
);

/// <summary>
/// Request body for one tier's provider/model/key selection.
/// </summary>
public sealed record LlmTierSettingsRequest(
    [property: Required, MaxLength(100)] string Provider,
    [property: Required, MaxLength(100)] string Model,
    [property: MaxLength(128)] string? KeyId = null,
    [property: MaxLength(500)] string? Endpoint = null
);

/// <summary>
/// Key metadata returned by the API. Plaintext is intentionally omitted.
/// </summary>
public sealed record LlmApiKeyResponse(
    string Id,
    string? Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>
/// Request body for creating a tenant-owned LLM API key.
/// </summary>
public sealed record CreateLlmApiKeyRequest(
    [property: MaxLength(200)] string? Name,
    [property: Required, MaxLength(4096)] string ApiKey
);

/// <summary>
/// Request body for rotating an existing tenant-owned LLM API key.
/// </summary>
public sealed record RotateLlmApiKeyRequest([property: Required, MaxLength(4096)] string ApiKey);

/// <summary>
/// Request body for renaming an existing tenant-owned LLM API key.
/// </summary>
public sealed record RenameLlmApiKeyRequest([property: MaxLength(200)] string? Name);

/// <summary>
/// Request body for testing provider/model/key access.
/// </summary>
public sealed record TestLlmSettingsRequest(
    [property: Required, MaxLength(20)] string Tier,
    [property: Required, MaxLength(100)] string Provider,
    [property: Required, MaxLength(100)] string Model,
    [property: MaxLength(128)] string? KeyId = null,
    [property: MaxLength(20_000)] string? Prompt = null,
    [property: MaxLength(500)] string? Endpoint = null
);

/// <summary>
/// Error response for LLM settings API failures.
/// </summary>
public sealed record LlmSettingsError(string Message);

internal static class LlmSettingsContractMapping
{
    public static LlmSettingsConfigurationResponse ToResponse(
        OrganizationLlmConfiguration configuration
    ) =>
        new(
            ToResponse(configuration.Fast),
            ToResponse(configuration.High),
            ToResponse(configuration.Max)
        );

    public static OrganizationLlmConfiguration ToConfiguration(SaveLlmSettingsRequest request) =>
        new()
        {
            Fast = ToConfiguration(request.Fast),
            High = ToConfiguration(request.High),
            Max = ToConfiguration(request.Max),
        };

    public static LlmApiKeyResponse ToResponse(EncryptedValue value) =>
        new(value.Id, value.Name, value.CreatedAtUtc, value.UpdatedAtUtc);

    public static IReadOnlyList<string> ReferencedKeyIds(
        OrganizationLlmConfiguration configuration
    ) =>
        [
            .. new[] { configuration.Fast.KeyId, configuration.High.KeyId, configuration.Max.KeyId }
                .Where(keyId => !string.IsNullOrWhiteSpace(keyId))
                .Select(keyId => keyId!.Trim())
                .Distinct(StringComparer.Ordinal),
        ];

    private static LlmTierSettingsResponse ToResponse(
        OrganizationLlmTierConfiguration configuration
    ) => new(configuration.Provider, configuration.Model, configuration.KeyId, configuration.Endpoint);

    private static OrganizationLlmTierConfiguration ToConfiguration(
        LlmTierSettingsRequest request
    ) =>
        new()
        {
            Provider = request.Provider.Trim(),
            Model = request.Model.Trim(),
            KeyId = string.IsNullOrWhiteSpace(request.KeyId) ? null : request.KeyId.Trim(),
            Endpoint = string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim(),
        };
}
