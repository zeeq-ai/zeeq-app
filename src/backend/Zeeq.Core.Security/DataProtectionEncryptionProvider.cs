using Microsoft.AspNetCore.DataProtection;

namespace Zeeq.Core.Security;

/// <summary>
/// Local-development encryption provider backed by ASP.NET Core Data Protection.
/// </summary>
public sealed class DataProtectionEncryptionProvider(IDataProtectionProvider dataProtectionProvider)
    : IDataEncryptionProvider
{
    // NOTE: Kept as the original "Zeeq.Core.Llm.*" purpose string after the zeeq-ai/zeeq-app#192
    // move to Zeeq.Core.Security — Data Protection purpose strings are part of key derivation, so
    // changing this would make every ciphertext a local dev already has on disk unrecoverable.
    private const string Purpose = "Zeeq.Core.Llm.EncryptedValue.v1";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    /// <inheritdoc />
    public string ProviderName => DataEncryptionProviders.DataProtection;

    /// <inheritdoc />
    public Task<byte[]> EncryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_protector.Protect(plaintext.ToArray()));
    }

    /// <inheritdoc />
    public Task<byte[]> DecryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_protector.Unprotect(ciphertext.ToArray()));
    }
}
