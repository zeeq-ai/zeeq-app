using Microsoft.AspNetCore.DataProtection;

namespace Zeeq.Core.Llm;

/// <summary>
/// Local-development encryption provider backed by ASP.NET Core Data Protection.
/// </summary>
public sealed class DataProtectionEncryptionProvider(IDataProtectionProvider dataProtectionProvider)
    : IDataEncryptionProvider
{
    private const string Purpose = "Zeeq.Core.Llm.EncryptedValue.v1";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    /// <inheritdoc />
    public string ProviderName => LlmEncryptionProviders.DataProtection;

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
