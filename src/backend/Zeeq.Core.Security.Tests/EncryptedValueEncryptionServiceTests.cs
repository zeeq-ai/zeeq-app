using System.Text;
using Zeeq.Core.Models;

namespace Zeeq.Core.Security.Tests;

/// <summary>
/// Tests provider-neutral encrypted-value creation and decryption.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Core.Security.Tests --output detailed --disable-logo --treenode-filter "/*/*/EncryptedValueEncryptionServiceTests/*"
/// </summary>
public sealed class EncryptedValueEncryptionServiceTests
{
    private const string OrganizationId = "org_test";
    private readonly FakeDataEncryptionProvider _primary = new("primary");
    private readonly FakeDataEncryptionProvider _secondary = new("secondary");
    private readonly DateTimeOffset _now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task EncryptAsync_SecretString_ReturnsUnsavedEncryptedRow()
    {
        var service = CreateService("primary");

        var value = await service.EncryptAsync(
            OrganizationId,
            EncryptedValueKind.SecretString,
            "Notion webhook verification token",
            "secret-value",
            _now,
            CancellationToken.None
        );

        await Assert.That(value.Id).StartsWith("enc_");
        await Assert.That(value.OrganizationId).IsEqualTo(OrganizationId);
        await Assert.That(value.Kind).IsEqualTo(EncryptedValueKind.SecretString);
        await Assert.That(value.EncryptionProvider).IsEqualTo("primary");
        await Assert.That(Encoding.UTF8.GetString(value.Ciphertext)).DoesNotContain("secret-value");
    }

    [Test]
    public async Task DecryptAsync_UsesProviderPersistedOnRow()
    {
        var service = CreateService("primary");
        var value = await service.EncryptAsync(
            OrganizationId,
            EncryptedValueKind.SecretString,
            name: null,
            "secret-value",
            _now,
            CancellationToken.None
        );
        var afterProviderChange = CreateService("secondary");

        var plaintext = await afterProviderChange.DecryptAsync(
            value,
            EncryptedValueKind.SecretString,
            CancellationToken.None
        );

        await Assert.That(plaintext).IsEqualTo("secret-value");
        await Assert.That(_primary.DecryptCount).IsEqualTo(1);
        await Assert.That(_secondary.DecryptCount).IsEqualTo(0);
    }

    [Test]
    public async Task DecryptAsync_WithUnexpectedKind_ReturnsNullWithoutDecrypting()
    {
        var service = CreateService("primary");
        var value = await service.EncryptAsync(
            OrganizationId,
            EncryptedValueKind.SecretString,
            name: null,
            "secret-value",
            _now,
            CancellationToken.None
        );

        var plaintext = await service.DecryptAsync(
            value,
            EncryptedValueKind.LlmApiKey,
            CancellationToken.None
        );

        await Assert.That(plaintext).IsNull();
        await Assert.That(_primary.DecryptCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReencryptAsync_WithUnexpectedKind_DoesNotMutateRow()
    {
        var service = CreateService("primary");
        var value = await service.EncryptAsync(
            OrganizationId,
            EncryptedValueKind.SecretString,
            name: null,
            "original",
            _now,
            CancellationToken.None
        );
        var originalCiphertext = value.Ciphertext.ToArray();

        var reencrypted = await service.ReencryptAsync(
            value,
            EncryptedValueKind.LlmApiKey,
            "replacement",
            _now.AddMinutes(1),
            CancellationToken.None
        );

        await Assert.That(reencrypted).IsFalse();
        await Assert.That(value.Ciphertext).IsEquivalentTo(originalCiphertext);
        await Assert.That(value.UpdatedAtUtc).IsEqualTo(_now);
    }

    [Test]
    public async Task ReencryptAsync_WhenEncryptionFails_DoesNotMutateRow()
    {
        var originalService = CreateService("primary");
        var value = await originalService.EncryptAsync(
            OrganizationId,
            EncryptedValueKind.SecretString,
            name: null,
            "original",
            _now,
            CancellationToken.None
        );
        var originalCiphertext = value.Ciphertext.ToArray();
        var failingService = new EncryptedValueEncryptionService(
            TestSecuritySettings("failing"),
            [_primary, new FailingDataEncryptionProvider()]
        );

        async Task Act() =>
            await failingService.ReencryptAsync(
                value,
                EncryptedValueKind.SecretString,
                "replacement",
                _now.AddMinutes(1),
                CancellationToken.None
            );

        await Assert.That(Act).Throws<InvalidOperationException>();
        await Assert.That(value.EncryptionProvider).IsEqualTo("primary");
        await Assert.That(value.Ciphertext).IsEquivalentTo(originalCiphertext);
        await Assert.That(value.UpdatedAtUtc).IsEqualTo(_now);
    }

    private EncryptedValueEncryptionService CreateService(string activeProvider) =>
        new(TestSecuritySettings(activeProvider), [_primary, _secondary]);

    private static SecuritySettings TestSecuritySettings(string activeProvider) =>
        new()
        {
            EncryptionProvider = activeProvider,
            DataProtectionKeyRingPath = string.Empty,
            GoogleKmsKeyName = string.Empty,
        };

    private sealed class FakeDataEncryptionProvider(string providerName) : IDataEncryptionProvider
    {
        public string ProviderName { get; } = providerName;

        public int DecryptCount { get; private set; }

        public Task<byte[]> EncryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Encoding.UTF8.GetBytes(
                    $"{ProviderName}:{Convert.ToBase64String(plaintext.ToArray())}"
                )
            );

        public Task<byte[]> DecryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> ciphertext,
            CancellationToken cancellationToken
        )
        {
            DecryptCount++;
            var encoded = Encoding.UTF8.GetString(ciphertext.Span);
            var prefix = $"{ProviderName}:";

            if (!encoded.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Ciphertext was not encrypted by {ProviderName}."
                );
            }

            return Task.FromResult(Convert.FromBase64String(encoded[prefix.Length..]));
        }
    }

    private sealed class FailingDataEncryptionProvider : IDataEncryptionProvider
    {
        public string ProviderName => "failing";

        public Task<byte[]> EncryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Encryption failed.");

        public Task<byte[]> DecryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> ciphertext,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }
}
