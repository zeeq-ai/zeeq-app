using System.Text;
using Zeeq.Core.Common;
using Zeeq.Core.Llm;
using Zeeq.Core.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Zeeq.Platform.CodeReviews.Tests;

/// <summary>
/// Tests code-review LLM tier resolution.
///
/// dotnet run --project src/backend/Zeeq.Platform.CodeReviews.Tests --output detailed --disable-logo --treenode-filter "/*/*/CodeReviewLlmTierResolverTests/*"
/// </summary>
public sealed class CodeReviewLlmTierResolverTests
{
    private const string OrganizationId = "org_123";

    [Test]
    public async Task ResolveChatClientAsync_WithNoOrganizationConfiguration_UsesSystemDefaultTierClient()
    {
        var fixture = new Fixture();

        var resolved = await fixture.Resolver.ResolveChatClientAsync(
            OrganizationId,
            CodeReviewModelTier.High,
            CancellationToken.None
        );

        await Assert.That(ReferenceEquals(resolved.ChatClient, fixture.DefaultHighClient)).IsTrue();
        await Assert.That(resolved.Tier).IsEqualTo(CodeReviewModelTier.High);
        await Assert.That(resolved.Provider).IsEqualTo("Fireworks");
        await Assert.That(resolved.Model).IsEqualTo("accounts/fireworks/models/glm-5p2");
        await Assert
            .That(resolved.CredentialSource)
            .IsEqualTo(CodeReviewLlmCredentialSource.SystemDefault);
        await Assert.That(fixture.ClientFactory.CreatedTenantConfigurations).IsEmpty();
        await Assert.That(fixture.ClientFactory.CreatedDefaultConfigurations).IsEmpty();
    }

    [Test]
    public async Task ResolveChatClientAsync_WithOrganizationManagedKey_DecryptsKeyAndCreatesTenantClient()
    {
        var fixture = new Fixture
        {
            OrganizationConfiguration = new()
            {
                Fast = OrganizationLlmConfiguration.Default.Fast,
                High = new()
                {
                    Provider = "OpenAI",
                    Model = "gpt-4.1",
                    KeyId = "enc_openai",
                },
                Max = OrganizationLlmConfiguration.Default.Max,
            },
        };
        await fixture.StoreKeyAsync("enc_openai", "sk-tenant-openai");

        var resolved = await fixture.Resolver.ResolveChatClientAsync(
            OrganizationId,
            CodeReviewModelTier.High,
            CancellationToken.None
        );

        var configuration = fixture.ClientFactory.CreatedTenantConfigurations.Single();

        await Assert
            .That(ReferenceEquals(resolved.ChatClient, fixture.ClientFactory.TenantClient))
            .IsTrue();
        await Assert.That(configuration.Provider).IsEqualTo("OpenAI");
        await Assert.That(configuration.Model).IsEqualTo("gpt-4.1");
        await Assert.That(configuration.ApiKey).IsEqualTo("sk-tenant-openai");
        await Assert.That(configuration.KeySource).IsEqualTo("organization-managed-key");
        await Assert.That(configuration.Endpoint).IsEqualTo("");
        await Assert
            .That(resolved.CredentialSource)
            .IsEqualTo(CodeReviewLlmCredentialSource.OrganizationManagedKey);
    }

    [Test]
    public async Task ResolveChatClientAsync_WithOrganizationFireworksAndNoKey_UsesInternalDefaultKeyForConfiguredModel()
    {
        var fixture = new Fixture
        {
            OrganizationConfiguration = new()
            {
                Fast = OrganizationLlmConfiguration.Default.Fast,
                High = new()
                {
                    Provider = "Fireworks",
                    Model = "accounts/fireworks/models/glm-5p2",
                },
                Max = OrganizationLlmConfiguration.Default.Max,
            },
        };

        var resolved = await fixture.Resolver.ResolveChatClientAsync(
            OrganizationId,
            CodeReviewModelTier.High,
            CancellationToken.None
        );

        var configuration = fixture.ClientFactory.CreatedDefaultConfigurations.Single();

        await Assert
            .That(ReferenceEquals(resolved.ChatClient, fixture.ClientFactory.InternalDefaultClient))
            .IsTrue();
        await Assert.That(configuration.Provider).IsEqualTo("Fireworks");
        await Assert.That(configuration.Model).IsEqualTo("accounts/fireworks/models/glm-5p2");
        await Assert.That(configuration.ApiKey).IsEqualTo("app-high-key");
        await Assert.That(configuration.Endpoint).IsEqualTo("https://api.fireworks.test");
        await Assert
            .That(resolved.CredentialSource)
            .IsEqualTo(CodeReviewLlmCredentialSource.InternalDefaultKey);
    }

    [Test]
    public async Task ResolveChatClientAsync_WithWhitespaceInOrganizationTier_NormalizesProviderAndModel()
    {
        var fixture = new Fixture
        {
            OrganizationConfiguration = new()
            {
                Fast = OrganizationLlmConfiguration.Default.Fast,
                High = new()
                {
                    Provider = " Fireworks ",
                    Model = " accounts/fireworks/models/glm-5p2 ",
                },
                Max = OrganizationLlmConfiguration.Default.Max,
            },
        };

        var resolved = await fixture.Resolver.ResolveChatClientAsync(
            OrganizationId,
            CodeReviewModelTier.High,
            CancellationToken.None
        );

        var configuration = fixture.ClientFactory.CreatedDefaultConfigurations.Single();

        await Assert.That(configuration.Provider).IsEqualTo("Fireworks");
        await Assert.That(configuration.Model).IsEqualTo("accounts/fireworks/models/glm-5p2");
        await Assert.That(resolved.Provider).IsEqualTo("Fireworks");
        await Assert.That(resolved.Model).IsEqualTo("accounts/fireworks/models/glm-5p2");
    }

    [Test]
    public async Task ResolveChatClientAsync_WithNonFireworksAndNoKey_ThrowsConfigurationError()
    {
        var fixture = new Fixture
        {
            OrganizationConfiguration = new()
            {
                Fast = OrganizationLlmConfiguration.Default.Fast,
                High = new() { Provider = "OpenAI", Model = "gpt-4.1" },
                Max = OrganizationLlmConfiguration.Default.Max,
            },
        };

        async Task Act()
        {
            _ = await fixture.Resolver.ResolveChatClientAsync(
                OrganizationId,
                CodeReviewModelTier.High,
                CancellationToken.None
            );
        }

        await Assert
            .That(Act)
            .Throws<InvalidOperationException>()
            .WithMessage(
                "Code-review tier 'High' requires a managed API key for provider 'OpenAI'."
            );
    }

    [Test]
    public async Task ResolveChatClientAsync_WithOrganizationDeepSeekOnFireworksAndNoKey_UsesInternalDefaultKey()
    {
        var fixture = new Fixture
        {
            OrganizationConfiguration = new()
            {
                Fast = OrganizationLlmConfiguration.Default.Fast,
                High = new()
                {
                    Provider = "Fireworks",
                    Model = "accounts/fireworks/models/deepseek-v4-pro",
                },
                Max = OrganizationLlmConfiguration.Default.Max,
            },
        };

        var resolved = await fixture.Resolver.ResolveChatClientAsync(
            OrganizationId,
            CodeReviewModelTier.High,
            CancellationToken.None
        );

        var configuration = fixture.ClientFactory.CreatedDefaultConfigurations.Single();

        await Assert.That(configuration.Provider).IsEqualTo("Fireworks");
        await Assert
            .That(configuration.Model)
            .IsEqualTo("accounts/fireworks/models/deepseek-v4-pro");
        await Assert.That(configuration.Endpoint).IsEqualTo("https://api.fireworks.test");
        await Assert
            .That(resolved.CredentialSource)
            .IsEqualTo(CodeReviewLlmCredentialSource.InternalDefaultKey);
    }

    private sealed class Fixture
    {
        private readonly FakeEncryptedValueStore _encryptedValues = new();
        private readonly FakeDataEncryptionProvider _encryptionProvider = new("test-encryption");
        private readonly KeyEncryptionService _keyEncryption;

        public Fixture()
        {
            _keyEncryption = new(
                LlmSettings with
                {
                    EncryptionProvider = _encryptionProvider.ProviderName,
                },
                _encryptedValues,
                [_encryptionProvider],
                new MemoryCache(new MemoryCacheOptions())
            );

            Resolver = new(
                SettingsStore,
                _keyEncryption,
                ClientFactory,
                new(DefaultFastClient, DefaultHighClient, DefaultMaxClient),
                LlmSettings,
                NullLogger<CodeReviewLlmTierResolver>.Instance
            );
        }

        public OrganizationLlmConfiguration? OrganizationConfiguration
        {
            get => SettingsStore.Configuration;
            init => SettingsStore.Configuration = value;
        }

        public FakeLlmSettingsStore SettingsStore { get; } = new();

        public CapturingLlmClientFactory ClientFactory { get; } = new();

        public IChatClient DefaultFastClient { get; } = Substitute.For<IChatClient>();

        public IChatClient DefaultHighClient { get; } = Substitute.For<IChatClient>();

        public IChatClient DefaultMaxClient { get; } = Substitute.For<IChatClient>();

        public LlmSettings LlmSettings { get; } =
            new()
            {
                Models = new()
                {
                    Fast = new()
                    {
                        Provider = "Fireworks",
                        Model = "accounts/fireworks/models/glm-5p2",
                        ApiKey = "app-fast-key",
                        Endpoint = "https://api.fireworks.test",
                    },
                    High = new()
                    {
                        Provider = "Fireworks",
                        Model = "accounts/fireworks/models/glm-5p2",
                        ApiKey = "app-high-key",
                        Endpoint = "https://api.fireworks.test",
                    },
                    Max = new()
                    {
                        Provider = "Fireworks",
                        Model = "accounts/fireworks/models/glm-5p2",
                        ApiKey = "app-max-key",
                        Endpoint = "https://api.fireworks.test",
                    },
                },
            };

        public CodeReviewLlmTierResolver Resolver { get; }

        public async Task StoreKeyAsync(string keyId, string plaintext)
        {
            var ciphertext = await _encryptionProvider.EncryptAsync(
                OrganizationId,
                Encoding.UTF8.GetBytes(plaintext),
                CancellationToken.None
            );
            await _encryptedValues.AddAsync(
                new()
                {
                    Id = keyId,
                    OrganizationId = OrganizationId,
                    Kind = EncryptedValueKind.LlmApiKey,
                    EncryptionProvider = _encryptionProvider.ProviderName,
                    Ciphertext = ciphertext,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
                CancellationToken.None
            );
        }
    }

    private sealed class FakeLlmSettingsStore : ILlmSettingsStore
    {
        public OrganizationLlmConfiguration? Configuration { get; set; }

        public Task<OrganizationLlmConfiguration?> FindConfigurationAsync(
            string organizationId,
            CancellationToken cancellationToken
        ) => Task.FromResult(Configuration);

        public Task<bool> UpdateConfigurationAsync(
            string organizationId,
            OrganizationLlmConfiguration configuration,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();
    }

    private sealed class CapturingLlmClientFactory : ILlmClientFactory
    {
        public IChatClient TenantClient { get; } = Substitute.For<IChatClient>();

        public IChatClient InternalDefaultClient { get; } = Substitute.For<IChatClient>();

        public List<ResolvedLlmConfiguration> CreatedTenantConfigurations { get; } = [];

        public List<LlmModelDefault> CreatedDefaultConfigurations { get; } = [];

        public IChatClient CreateChatClient(ResolvedLlmConfiguration configuration)
        {
            CreatedTenantConfigurations.Add(configuration);
            return TenantClient;
        }

        public IChatClient CreateDefaultChatClient(LlmModelDefault configuration)
        {
            CreatedDefaultConfigurations.Add(configuration);
            return InternalDefaultClient;
        }

        public AIAgent CreateAgent(ResolvedLlmConfiguration configuration) =>
            throw new NotSupportedException();

        public IEmbeddingGenerator<string, Embedding<float>> CreateDefaultEmbeddingGenerator(
            LlmEmbeddingSettings settings,
            EmbeddingClientProfile profile
        ) => throw new NotSupportedException();
    }

    private sealed class FakeEncryptedValueStore : IEncryptedValueStore
    {
        private readonly Dictionary<(string OrganizationId, string Id), EncryptedValue> _values =
        [];

        public Task<IReadOnlyList<EncryptedValue>> ListActiveAsync(
            string organizationId,
            EncryptedValueKind kind,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult<IReadOnlyList<EncryptedValue>>([
                .. _values
                    .Values.Where(value =>
                        value.OrganizationId == organizationId
                        && value.Kind == kind
                        && value.DisabledAtUtc is null
                    )
                    .Select(Clone),
            ]);

        public Task<EncryptedValue?> FindActiveAsync(
            string organizationId,
            string id,
            CancellationToken cancellationToken
        )
        {
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

        public Task<bool> UpdateAsync(EncryptedValue value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DisableAsync(
            string organizationId,
            string id,
            DateTimeOffset disabledAtUtc,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

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
