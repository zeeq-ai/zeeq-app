using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Core.Llm;

/// <summary>
/// Encrypts and decrypts organization-owned <see cref="EncryptedValue"/> payloads.
/// </summary>
/// <remarks>
/// This service owns provider selection but deliberately does not persist rows. Callers can
/// therefore include a newly encrypted value in a larger domain transaction, such as atomically
/// linking a webhook verification secret to its owning library. Decryption always follows the
/// provider recorded on the row so existing values survive active-provider changes.
/// </remarks>
public sealed class EncryptedValueEncryptionService(
    LlmSettings settings,
    IEnumerable<IDataEncryptionProvider> providers
)
{
    private readonly IReadOnlyDictionary<string, IDataEncryptionProvider> _providers =
        providers.ToDictionary(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates an unsaved encrypted-value row for arbitrary secret text.
    /// </summary>
    public async Task<EncryptedValue> EncryptAsync(
        string organizationId,
        EncryptedValueKind kind,
        string? name,
        string plaintext,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    )
    {
        var provider = GetProvider(settings.EncryptionProvider);
        var ciphertext = await EncryptAsync(provider, organizationId, plaintext, cancellationToken);

        return new EncryptedValue
        {
            Id = $"enc_{Guid.CreateVersion7():N}",
            OrganizationId = organizationId,
            Kind = kind,
            EncryptionProvider = provider.ProviderName,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Ciphertext = ciphertext,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
    }

    /// <summary>
    /// Re-encrypts an existing row with the active provider when its kind matches the caller's
    /// expected kind.
    /// </summary>
    public async Task<bool> ReencryptAsync(
        EncryptedValue value,
        EncryptedValueKind expectedKind,
        string plaintext,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Kind != expectedKind)
        {
            return false;
        }

        var provider = GetProvider(settings.EncryptionProvider);
        var ciphertext = await EncryptAsync(
            provider,
            value.OrganizationId,
            plaintext,
            cancellationToken
        );

        value.EncryptionProvider = provider.ProviderName;
        value.Ciphertext = ciphertext;
        value.UpdatedAtUtc = nowUtc;

        return true;
    }

    /// <summary>
    /// Decrypts a row using its persisted provider when its kind matches the caller's expected
    /// kind.
    /// </summary>
    public async Task<string?> DecryptAsync(
        EncryptedValue value,
        EncryptedValueKind expectedKind,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Kind != expectedKind)
        {
            return null;
        }

        var provider = GetProvider(value.EncryptionProvider);
        var plaintextBytes = await provider.DecryptAsync(
            value.OrganizationId,
            value.Ciphertext,
            cancellationToken
        );

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private static async Task<byte[]> EncryptAsync(
        IDataEncryptionProvider provider,
        string organizationId,
        string plaintext,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new ArgumentException("Plaintext secret is required.", nameof(plaintext));
        }

        return await provider.EncryptAsync(
            organizationId,
            Encoding.UTF8.GetBytes(plaintext),
            cancellationToken
        );
    }

    private IDataEncryptionProvider GetProvider(string providerName)
    {
        if (_providers.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"No data encryption provider is registered for '{providerName}'."
        );
    }
}
