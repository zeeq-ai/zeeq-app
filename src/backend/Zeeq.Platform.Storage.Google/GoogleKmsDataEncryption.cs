using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Google.Cloud.Kms.V1;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.Storage.Google;

/// <summary>
/// Registers Google Cloud KMS-backed data encryption.
/// </summary>
/// <remarks>
/// Production uses Cloud KMS because tenant-owned LLM API keys are customer
/// secrets that must be encrypted before they are stored in Postgres. Cloud Run
/// file systems are ephemeral and not shared across instances, so the local
/// Data Protection key-ring strategy is intentionally limited to Development.
/// With KMS, the key-encryption key remains inside Google Cloud, access is
/// controlled through IAM, key versions can be rotated or disabled centrally,
/// and KMS use is visible through the cloud control plane.
///
/// This adapter uses direct symmetric Cloud KMS encrypt/decrypt operations for
/// small secret values. It does not implement envelope encryption because the
/// plaintext payload is an API key string, not a large document or blob that
/// needs local data-encryption keys. If Zeeq later encrypts larger values,
/// envelope encryption should be reconsidered so only data-encryption keys are
/// wrapped by KMS.
///
/// Cost is intentionally bounded by the application design. KMS operations are
/// charged by active key versions and key-use operations, so Zeeq calls KMS on
/// key create/rotate and on cache misses during decrypt; decrypted plaintext is
/// cached briefly inside <see cref="KeyEncryptionService" /> and is never stored,
/// logged, or used as a cache key.
///
/// References:
/// <see href="https://docs.cloud.google.com/kms/docs/encrypt-decrypt" />,
/// <see href="https://docs.cloud.google.com/kms/docs/envelope-encryption" />,
/// and <see href="https://cloud.google.com/kms/pricing" />.
/// </remarks>
public static class GoogleKmsDataEncryption
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the Cloud KMS encryption provider when configured as the active data encryption provider.
        /// </summary>
        public IServiceCollection AddGoogleKmsDataEncryption(LlmSettings settings)
        {
            if (
                !settings.EncryptionProvider.Equals(
                    LlmEncryptionProviders.CloudKms,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return services;
            }

            services.AddSingleton<IGoogleKmsClient>(_ => new GoogleCloudKmsClient(
                KeyManagementServiceClient.Create()
            ));

            services.AddSingleton<IDataEncryptionProvider, GoogleKmsEncryptionProvider>();

            return services;
        }
    }
}

/// <summary>
/// Minimal Cloud KMS client boundary used by the LLM encryption provider.
/// </summary>
public interface IGoogleKmsClient
{
    /// <summary>
    /// Encrypts bytes with the configured KMS crypto key.
    /// </summary>
    Task<byte[]> EncryptAsync(
        string keyName,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Decrypts bytes with the configured KMS crypto key.
    /// </summary>
    Task<byte[]> DecryptAsync(
        string keyName,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Google Cloud KMS SDK adapter.
/// </summary>
/// <remarks>
/// This type is the only direct dependency on <see cref="KeyManagementServiceClient" />
/// in the LLM key-encryption path. Tests use <see cref="IGoogleKmsClient" /> so
/// the default suite remains hermetic and the live KMS round trip stays opt-in.
/// </remarks>
public sealed class GoogleCloudKmsClient(KeyManagementServiceClient client) : IGoogleKmsClient
{
    /// <inheritdoc />
    public async Task<byte[]> EncryptAsync(
        string keyName,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken
    )
    {
        var response = await client.EncryptAsync(
            keyName,
            ByteString.CopyFrom(plaintext.Span),
            cancellationToken
        );

        return response.Ciphertext.ToByteArray();
    }

    /// <inheritdoc />
    public async Task<byte[]> DecryptAsync(
        string keyName,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken
    )
    {
        var response = await client.DecryptAsync(
            keyName,
            ByteString.CopyFrom(ciphertext.Span),
            cancellationToken
        );

        return response.Plaintext.ToByteArray();
    }
}

/// <summary>
/// LLM data-encryption provider backed by Google Cloud KMS.
/// </summary>
/// <remarks>
/// The provider encrypts tenant-owned API key bytes before persistence and
/// decrypts them only for the short-lived server-side call path. It records the
/// stable provider name <see cref="LlmEncryptionProviders.CloudKms" /> on each
/// encrypted row through <see cref="KeyEncryptionService" />, allowing old rows
/// to keep decrypting with KMS even if the active provider setting changes for
/// future writes.
/// </remarks>
public sealed class GoogleKmsEncryptionProvider(LlmSettings settings, IGoogleKmsClient client)
    : IDataEncryptionProvider
{
    private readonly string _keyName = string.IsNullOrWhiteSpace(settings.GoogleKmsKeyName)
        ? throw new InvalidOperationException(
            "AppSettings:Llm:GoogleKmsKeyName is required for cloud-kms encryption."
        )
        : settings.GoogleKmsKeyName.Trim();

    /// <inheritdoc />
    public string ProviderName => LlmEncryptionProviders.CloudKms;

    /// <inheritdoc />
    public Task<byte[]> EncryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken
    ) => client.EncryptAsync(_keyName, plaintext, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> DecryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken
    ) => client.DecryptAsync(_keyName, ciphertext, cancellationToken);
}
