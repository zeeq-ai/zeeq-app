using Microsoft.Extensions.Caching.Memory;
using Zeeq.Core.Models;

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
    EncryptedValueEncryptionService encryption,
    IEncryptedValueStore encryptedValues,
    IMemoryCache memoryCache
)
{
    private static readonly TimeSpan PlaintextCacheTtl = TimeSpan.FromMinutes(30);

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
        var value = await encryption.EncryptAsync(
            organizationId,
            EncryptedValueKind.LlmApiKey,
            name,
            plaintextApiKey,
            nowUtc,
            cancellationToken
        );

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

        if (
            !await encryption.ReencryptAsync(
                existing,
                EncryptedValueKind.LlmApiKey,
                plaintextApiKey,
                nowUtc,
                cancellationToken
            )
        )
        {
            return false;
        }

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

        var plaintext = await encryption.DecryptAsync(
            value,
            EncryptedValueKind.LlmApiKey,
            cancellationToken
        );
        if (plaintext is null)
        {
            return null;
        }

        memoryCache.Set(cacheKey, plaintext, PlaintextCacheTtl);
        return plaintext;
    }

    private static string BuildCacheKey(EncryptedValue value) =>
        $"llm-settings:key:{value.OrganizationId}:{value.Id}:{value.UpdatedAtUtc.Ticks}";
}
