using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.Core.Security;

namespace Zeeq.Platform.Storage.Aws;

/// <summary>Registers AWS KMS when configured, including decryption after a provider switch; credentials come from the SDK credential chain.</summary>
public static class AwsKmsDataEncryption
{
    extension(IServiceCollection services)
    {
        /// <summary>Adds the organization-bound KMS encryption provider.</summary>
        public IServiceCollection AddAwsKmsDataEncryption(SecuritySettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.AwsKmsKeyArn))
            {
                services.AddSingleton<IAmazonKeyManagementService>(
                    _ => new AmazonKeyManagementServiceClient()
                );
                services.AddSingleton<IDataEncryptionProvider, AwsKmsEncryptionProvider>();
            }
            return services;
        }
    }
}

/// <summary>Encrypts small tenant secrets with a required organization encryption context.</summary>
/// <remarks>
/// Context is nonsecret. A different organization cannot decrypt the ciphertext.
/// KMS key-material rotation preserves the key ARN and existing ciphertext. Replacing the ARN
/// requires ciphertext migration before switching configuration; decryption is bound to that key.
/// </remarks>
public sealed class AwsKmsEncryptionProvider(
    SecuritySettings settings,
    IAmazonKeyManagementService client
) : IDataEncryptionProvider
{
    /// <inheritdoc />
    public string ProviderName => DataEncryptionProviders.AwsKms;

    /// <inheritdoc />
    public async Task<byte[]> EncryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken
    )
    {
        if (plaintext.Length is 0 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(plaintext),
                "AWS KMS symmetric plaintext must contain 1–4096 bytes."
            );
        }
        using var input = new MemoryStream(plaintext.ToArray());
        var response = await client.EncryptAsync(
            new EncryptRequest
            {
                KeyId = settings.AwsKmsKeyArn,
                Plaintext = input,
                EncryptionContext = Context(organizationId),
            },
            cancellationToken
        );
        using var output = response.CiphertextBlob;
        return output.ToArray();
    }

    /// <inheritdoc />
    public async Task<byte[]> DecryptAsync(
        string organizationId,
        ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken
    )
    {
        using var input = new MemoryStream(ciphertext.ToArray());
        // NOTE: Keep the intended key explicit, as AWS recommends. Arbitrary retained keys are not accepted.
        // https://docs.aws.amazon.com/kms/latest/APIReference/API_Decrypt.html
        var response = await client.DecryptAsync(
            new DecryptRequest
            {
                KeyId = settings.AwsKmsKeyArn,
                CiphertextBlob = input,
                EncryptionContext = Context(organizationId),
            },
            cancellationToken
        );
        using var output = response.Plaintext;
        return output.ToArray();
    }

    private static Dictionary<string, string> Context(string organizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        return new() { ["organizationId"] = organizationId };
    }
}
