using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Zeeq.Core.Llm.Tests;

/// <summary>
/// Unit tests for provider-neutral key encryption orchestration.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Core.Llm.Tests --output detailed --disable-logo --treenode-filter "/*/*/KeyEncryptionServiceTests/*"
/// </summary>
public sealed class KeyEncryptionServiceTests
{
    private const string OrganizationId = "org_test";
    private readonly FakeEncryptedValueStore _store = new();
    private readonly FakeDataEncryptionProvider _primaryProvider = new("test-primary");
    private readonly FakeDataEncryptionProvider _secondaryProvider = new("test-secondary");
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly DateTimeOffset _now = new(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task KeyEncryptionService_EncryptAndStoreKeyAsync_DoesNotStorePlaintext()
    {
        var service = CreateService(encryptionProvider: _primaryProvider.ProviderName);

        var value = await service.EncryptAndStoreKeyAsync(
            OrganizationId,
            name: "Production OpenAI",
            plaintextApiKey: "sk-secret-value",
            _now,
            CancellationToken.None
        );

        await Assert.That(value.Id).StartsWith("enc_");
        await Assert.That(value.Name).IsEqualTo("Production OpenAI");
        await Assert
            .That(Encoding.UTF8.GetString(value.Ciphertext))
            .DoesNotContain("sk-secret-value");
    }

    [Test]
    public async Task KeyEncryptionService_EncryptAndStoreKeyAsync_StoresActiveEncryptionProvider()
    {
        var service = CreateService(encryptionProvider: _secondaryProvider.ProviderName);

        var value = await service.EncryptAndStoreKeyAsync(
            OrganizationId,
            name: null,
            plaintextApiKey: "tenant-key",
            _now,
            CancellationToken.None
        );

        await Assert.That(value.EncryptionProvider).IsEqualTo(_secondaryProvider.ProviderName);
    }

    [Test]
    public async Task KeyEncryptionService_DecryptKeyAsync_UsesCacheOnSecondRead()
    {
        var service = CreateService(encryptionProvider: _primaryProvider.ProviderName);
        var value = await service.EncryptAndStoreKeyAsync(
            OrganizationId,
            name: null,
            plaintextApiKey: "tenant-key",
            _now,
            CancellationToken.None
        );

        var first = await service.DecryptKeyAsync(OrganizationId, value.Id, CancellationToken.None);
        var second = await service.DecryptKeyAsync(
            OrganizationId,
            value.Id,
            CancellationToken.None
        );

        await Assert.That(first).IsEqualTo("tenant-key");
        await Assert.That(second).IsEqualTo("tenant-key");
        await Assert.That(_primaryProvider.DecryptCount).IsEqualTo(1);
        await Assert.That(_store.FindActiveCount).IsEqualTo(2);
    }

    [Test]
    public async Task KeyEncryptionService_DecryptKeyAsync_WithUpdatedTimestamp_BypassesOldCache()
    {
        var service = CreateService(encryptionProvider: _primaryProvider.ProviderName);
        var value = await service.EncryptAndStoreKeyAsync(
            OrganizationId,
            name: null,
            plaintextApiKey: "tenant-key",
            _now,
            CancellationToken.None
        );

        var first = await service.DecryptKeyAsync(OrganizationId, value.Id, CancellationToken.None);

        _store.SetUpdatedAt(OrganizationId, value.Id, _now.AddMinutes(1));

        var second = await service.DecryptKeyAsync(
            OrganizationId,
            value.Id,
            CancellationToken.None
        );

        await Assert.That(first).IsEqualTo("tenant-key");
        await Assert.That(second).IsEqualTo("tenant-key");
        await Assert.That(_primaryProvider.DecryptCount).IsEqualTo(2);
    }

    [Test]
    public async Task KeyEncryptionService_DecryptKeyAsync_UsesRowEncryptionProviderWhenActiveProviderChanges()
    {
        var originalService = CreateService(encryptionProvider: _primaryProvider.ProviderName);
        var value = await originalService.EncryptAndStoreKeyAsync(
            OrganizationId,
            name: null,
            plaintextApiKey: "tenant-key",
            _now,
            CancellationToken.None
        );
        var serviceAfterConfigChange = CreateService(
            encryptionProvider: _secondaryProvider.ProviderName
        );

        var plaintext = await serviceAfterConfigChange.DecryptKeyAsync(
            OrganizationId,
            value.Id,
            CancellationToken.None
        );

        await Assert.That(plaintext).IsEqualTo("tenant-key");
        await Assert.That(_primaryProvider.DecryptCount).IsEqualTo(1);
        await Assert.That(_secondaryProvider.DecryptCount).IsEqualTo(0);
    }

    [Test]
    public async Task KeyEncryptionService_DecryptKeyAsync_WithDisabledKey_DoesNotReturnCachedPlaintext()
    {
        var service = CreateService(encryptionProvider: _primaryProvider.ProviderName);
        var value = await service.EncryptAndStoreKeyAsync(
            OrganizationId,
            name: null,
            plaintextApiKey: "tenant-key",
            _now,
            CancellationToken.None
        );
        var first = await service.DecryptKeyAsync(OrganizationId, value.Id, CancellationToken.None);

        await _store.DisableAsync(
            OrganizationId,
            value.Id,
            _now.AddMinutes(1),
            CancellationToken.None
        );

        var second = await service.DecryptKeyAsync(
            OrganizationId,
            value.Id,
            CancellationToken.None
        );

        await Assert.That(first).IsEqualTo("tenant-key");
        await Assert.That(second).IsNull();
        await Assert.That(_primaryProvider.DecryptCount).IsEqualTo(1);
    }

    [Test]
    public async Task KeyEncryptionService_DecryptKeyAsync_WithNonLlmKind_ReturnsNull()
    {
        var service = CreateService(encryptionProvider: _primaryProvider.ProviderName);
        var value = new EncryptedValue
        {
            Id = "enc_secret",
            OrganizationId = OrganizationId,
            Kind = EncryptedValueKind.SecretString,
            EncryptionProvider = _primaryProvider.ProviderName,
            Ciphertext = [1, 2, 3],
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        await _store.AddAsync(value, CancellationToken.None);

        var plaintext = await service.DecryptKeyAsync(
            OrganizationId,
            value.Id,
            CancellationToken.None
        );

        await Assert.That(plaintext).IsNull();
        await Assert.That(_primaryProvider.DecryptCount).IsEqualTo(0);
    }

    [Test]
    public async Task KeyEncryptionService_RotateKeyAsync_WithNonLlmKind_ReturnsFalse()
    {
        var service = CreateService(encryptionProvider: _primaryProvider.ProviderName);
        var value = new EncryptedValue
        {
            Id = "enc_secret",
            OrganizationId = OrganizationId,
            Kind = EncryptedValueKind.SecretString,
            EncryptionProvider = _primaryProvider.ProviderName,
            Ciphertext = [1, 2, 3],
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        await _store.AddAsync(value, CancellationToken.None);

        var rotated = await service.RotateKeyAsync(
            OrganizationId,
            value.Id,
            "new-key",
            _now.AddMinutes(1),
            CancellationToken.None
        );

        await Assert.That(rotated).IsFalse();
    }

    [Test]
    public async Task KeyEncryptionService_WithMissingProvider_ThrowsConfigurationError()
    {
        var service = new KeyEncryptionService(
            Settings("missing-provider"),
            _store,
            [_primaryProvider],
            _cache
        );

        await Assert
            .That(async () =>
                await service.EncryptAndStoreKeyAsync(
                    OrganizationId,
                    name: null,
                    plaintextApiKey: "tenant-key",
                    _now,
                    CancellationToken.None
                )
            )
            .Throws<InvalidOperationException>()
            .WithMessage("No LLM data encryption provider is registered for 'missing-provider'.");
    }

    private KeyEncryptionService CreateService(string encryptionProvider) =>
        new(Settings(encryptionProvider), _store, [_primaryProvider, _secondaryProvider], _cache);

    private static LlmSettings Settings(string encryptionProvider) =>
        new()
        {
            EncryptionProvider = encryptionProvider,
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

    private sealed class FakeEncryptedValueStore : IEncryptedValueStore
    {
        private readonly Dictionary<(string OrganizationId, string Id), EncryptedValue> _values =
        [];

        public int FindActiveCount { get; private set; }

        public Task<IReadOnlyList<EncryptedValue>> ListActiveAsync(
            string organizationId,
            EncryptedValueKind kind,
            CancellationToken cancellationToken
        )
        {
            var values = _values
                .Values.Where(value =>
                    value.OrganizationId == organizationId
                    && value.Kind == kind
                    && value.DisabledAtUtc is null
                )
                .Select(Clone)
                .ToArray();

            return Task.FromResult<IReadOnlyList<EncryptedValue>>(values);
        }

        public Task<EncryptedValue?> FindActiveAsync(
            string organizationId,
            string id,
            CancellationToken cancellationToken
        )
        {
            FindActiveCount++;
            var found =
                _values.TryGetValue((organizationId, id), out var value)
                && value.DisabledAtUtc is null
                    ? Clone(value)
                    : null;

            return Task.FromResult(found);
        }

        public Task<EncryptedValue> AddAsync(
            EncryptedValue value,
            CancellationToken cancellationToken
        )
        {
            _values.Add((value.OrganizationId, value.Id), Clone(value));
            return Task.FromResult(Clone(value));
        }

        public Task<bool> UpdateAsync(EncryptedValue value, CancellationToken cancellationToken)
        {
            if (!_values.TryGetValue((value.OrganizationId, value.Id), out var existing))
            {
                return Task.FromResult(false);
            }

            existing.EncryptionProvider = value.EncryptionProvider;
            existing.Name = value.Name;
            existing.Ciphertext = value.Ciphertext.ToArray();
            existing.UpdatedAtUtc = value.UpdatedAtUtc;

            return Task.FromResult(true);
        }

        public Task<bool> DisableAsync(
            string organizationId,
            string id,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        )
        {
            if (!_values.TryGetValue((organizationId, id), out var existing))
            {
                return Task.FromResult(false);
            }

            existing.DisabledAtUtc = disabledAtUtc;
            existing.UpdatedAtUtc = disabledAtUtc;

            return Task.FromResult(true);
        }

        public void SetUpdatedAt(string organizationId, string id, DateTimeOffset updatedAtUtc)
        {
            _values[(organizationId, id)].UpdatedAtUtc = updatedAtUtc;
        }

        private static EncryptedValue Clone(EncryptedValue value) =>
            new()
            {
                Id = value.Id,
                OrganizationId = value.OrganizationId,
                Kind = value.Kind,
                EncryptionProvider = value.EncryptionProvider,
                Name = value.Name,
                Ciphertext = value.Ciphertext.ToArray(),
                CreatedAtUtc = value.CreatedAtUtc,
                UpdatedAtUtc = value.UpdatedAtUtc,
                DisabledAtUtc = value.DisabledAtUtc,
            };
    }

    private sealed class FakeDataEncryptionProvider(string providerName) : IDataEncryptionProvider
    {
        public string ProviderName { get; } = providerName;

        public int DecryptCount { get; private set; }

        public Task<byte[]> EncryptAsync(
            string organizationId,
            ReadOnlyMemory<byte> plaintext,
            CancellationToken cancellationToken
        )
        {
            var base64Plaintext = Convert.ToBase64String(plaintext.ToArray());
            return Task.FromResult(Encoding.UTF8.GetBytes($"{ProviderName}:{base64Plaintext}"));
        }

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
}
