namespace Zeeq.Core.Llm;

/// <summary>
/// Encrypts and decrypts secret material before it is persisted.
/// </summary>
public interface IDataEncryptionProvider
{
    /// <summary>
    /// Stable provider name persisted with encrypted rows.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Encrypts plaintext bytes for the given organization.
    /// </summary>
    Task<byte[]> EncryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Decrypts ciphertext bytes for the given organization.
    /// </summary>
    Task<byte[]> DecryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken
    );
}
