using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Zeeq.Core.Llm;

/// <summary>
/// Encrypts, stores, and decrypts organization-owned API keys.
/// </summary>
/// <remarks>
/// The active encryption provider setting is only used for newly encrypted or
/// rotated rows. Decryption always routes through the provider persisted on the
/// encrypted-value row so old keys continue to work after an environment moves
/// from one encryption adapter to another.
/// </remarks>
public sealed class KeyEncryptionService(
    LlmSettings settings,
    IEncryptedValueStore encryptedValues,
    IEnumerable<IDataEncryptionProvider> providers,
    IMemoryCache memoryCache
)
{
    private static readonly TimeSpan PlaintextCacheTtl = TimeSpan.FromMinutes(30);
    private readonly IReadOnlyDictionary<string, IDataEncryptionProvider> _providers =
        providers.ToDictionary(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Encrypts a plaintext LLM API key and stores only ciphertext.
    /// </summary>
    public async Task<EncryptedValue> EncryptAndStoreKeyAsync(
        string organizationId,
        string? name,
        string plaintextApiKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(settings.EncryptionProvider);
        var ciphertext = await EncryptAsync(
            provider,
            organizationId,
            plaintextApiKey,
            cancellationToken
        );
        var value = new EncryptedValue
        {
            Id = $"enc_{Guid.CreateVersion7():N}",
            OrganizationId = organizationId,
            Kind = EncryptedValueKind.LlmApiKey,
            EncryptionProvider = provider.ProviderName,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Ciphertext = ciphertext,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

        return await encryptedValues.AddAsync(value, cancellationToken);
    }

    /// <summary>
    /// Re-encrypts an active row with the currently configured provider.
    /// </summary>
    public async Task<bool> RotateKeyAsync(
        string organizationId,
        string encryptedValueId,
        string plaintextApiKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    )
    {
        var existing = await encryptedValues.FindActiveAsync(
            organizationId,
            encryptedValueId,
            cancellationToken
        );

        if (existing is null || existing.Kind != EncryptedValueKind.LlmApiKey)
        {
            return false;
        }

        var provider = GetProvider(settings.EncryptionProvider);
        var ciphertext = await EncryptAsync(
            provider,
            organizationId,
            plaintextApiKey,
            cancellationToken
        );

        existing.EncryptionProvider = provider.ProviderName;
        existing.Ciphertext = ciphertext;
        existing.UpdatedAtUtc = nowUtc;

        return await encryptedValues.UpdateAsync(existing, cancellationToken);
    }

    /// <summary>
    /// Decrypts an active key row and caches plaintext for short-lived server-side reuse.
    /// </summary>
    public async Task<string?> DecryptKeyAsync(
        string organizationId,
        string encryptedValueId,
        CancellationToken cancellationToken
    )
    {
        var value = await encryptedValues.FindActiveAsync(
            organizationId,
            encryptedValueId,
            cancellationToken
        );

        if (value is null || value.Kind != EncryptedValueKind.LlmApiKey)
        {
            return null;
        }

        var cacheKey = BuildCacheKey(value);
        if (memoryCache.TryGetValue(cacheKey, out string? cachedPlaintext))
        {
            return cachedPlaintext;
        }

        var provider = GetProvider(value.EncryptionProvider);
        var plaintextBytes = await provider.DecryptAsync(
            organizationId,
            value.Ciphertext,
            cancellationToken
        );
        var plaintext = Encoding.UTF8.GetString(plaintextBytes);

        memoryCache.Set(cacheKey, plaintext, PlaintextCacheTtl);
        return plaintext;
    }

    private static async Task<byte[]> EncryptAsync(
        IDataEncryptionProvider provider,
        string organizationId,
        string plaintextApiKey,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(plaintextApiKey))
        {
            throw new ArgumentException("Plaintext API key is required.", nameof(plaintextApiKey));
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextApiKey);
        return await provider.EncryptAsync(organizationId, plaintextBytes, cancellationToken);
    }

    private IDataEncryptionProvider GetProvider(string providerName)
    {
        if (_providers.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"No LLM data encryption provider is registered for '{providerName}'."
        );
    }

    private static string BuildCacheKey(EncryptedValue value) =>
        $"llm-settings:key:{value.OrganizationId}:{value.Id}:{value.UpdatedAtUtc.Ticks}";
}
