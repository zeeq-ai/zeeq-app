using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Platform.Storage.Google;
using Google.Cloud.Kms.V1;

namespace Zeeq.Platform.Storage.Google.Tests;

/// <summary>
/// Unit and opt-in live tests for Cloud KMS-backed LLM key encryption.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Storage.Google.Tests --output detailed --disable-logo --treenode-filter "/*/*/GoogleKmsEncryptionProviderTests/*"
/// </summary>
public sealed class GoogleKmsEncryptionProviderTests
{
    private const string KmsKeyName =
        "projects/test/locations/global/keyRings/test/cryptoKeys/test-key";

    [Test]
    public async Task GoogleKmsEncryptionProvider_EncryptAsync_CallsConfiguredKeyName()
    {
        var client = new FakeGoogleKmsClient();
        var provider = new GoogleKmsEncryptionProvider(Settings(KmsKeyName), client);

        var ciphertext = await provider.EncryptAsync(
            "org_test",
            Encoding.UTF8.GetBytes("tenant-key"),
            CancellationToken.None
        );

        await Assert.That(Encoding.UTF8.GetString(ciphertext)).IsEqualTo("encrypted:tenant-key");
        await Assert.That(client.EncryptKeyName).IsEqualTo(KmsKeyName);
        await Assert.That(client.DecryptKeyName).IsNull();
    }

    [Test]
    public async Task GoogleKmsEncryptionProvider_DecryptAsync_CallsConfiguredKeyName()
    {
        var client = new FakeGoogleKmsClient();
        var provider = new GoogleKmsEncryptionProvider(Settings(KmsKeyName), client);

        var plaintext = await provider.DecryptAsync(
            "org_test",
            Encoding.UTF8.GetBytes("encrypted:tenant-key"),
            CancellationToken.None
        );

        await Assert.That(Encoding.UTF8.GetString(plaintext)).IsEqualTo("tenant-key");
        await Assert.That(client.DecryptKeyName).IsEqualTo(KmsKeyName);
        await Assert.That(client.EncryptKeyName).IsNull();
    }

    [Test]
    public async Task GoogleKmsEncryptionProvider_LiveRoundTrip_WithConfiguredKmsKey()
    {
        var keyName = Environment.GetEnvironmentVariable("ZEEQ_TEST_GOOGLE_KMS_KEY_NAME");
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return;
        }

        var provider = new GoogleKmsEncryptionProvider(
            Settings(keyName),
            new GoogleCloudKmsClient(KeyManagementServiceClient.Create())
        );
        var plaintext = $"zeeq-test-{Guid.CreateVersion7():N}";

        var ciphertext = await provider.EncryptAsync(
            "org_live_test",
            Encoding.UTF8.GetBytes(plaintext),
            CancellationToken.None
        );
        var decrypted = await provider.DecryptAsync(
            "org_live_test",
            ciphertext,
            CancellationToken.None
        );

        await Assert.That(Encoding.UTF8.GetString(decrypted)).IsEqualTo(plaintext);
    }

    private static LlmSettings Settings(string keyName) =>
        new()
        {
            EncryptionProvider = LlmEncryptionProviders.CloudKms,
            GoogleKmsKeyName = keyName,
            Models = new LlmModelDefaults
            {
                Fast = new LlmModelDefault
                {
                    ApiKey = "default-key",
                    Model = "deepseek-v4-flash",
                    Endpoint = "https://api.deepseek.com",
                },
            },
        };

    private sealed class FakeGoogleKmsClient : IGoogleKmsClient
    {
        public string? EncryptKeyName { get; private set; }

        public string? DecryptKeyName { get; private set; }

        public Task<byte[]> EncryptAsync(
            string keyName,
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken
        )
        {
            EncryptKeyName = keyName;
            return Task.FromResult(
                Encoding.UTF8.GetBytes($"encrypted:{Encoding.UTF8.GetString(plaintext.Span)}")
            );
        }

        public Task<byte[]> DecryptAsync(
            string keyName,
            ReadOnlyMemory<byte> ciphertext,
            CancellationToken cancellationToken
        )
        {
            DecryptKeyName = keyName;
            var value = Encoding.UTF8.GetString(ciphertext.Span);
            return Task.FromResult(Encoding.UTF8.GetBytes(value["encrypted:".Length..]));
        }
    }
}
