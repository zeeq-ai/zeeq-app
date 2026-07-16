using Zeeq.Core.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Core.Llm.Tests;

/// <summary>
/// Unit tests for LLM service registration and startup validation.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Core.Llm.Tests --output detailed --disable-logo
/// </summary>
public sealed class SetupLlmTests
{
    [Test]
    public async Task AddZeeqLlm_WithOnlyFastApiKey_ResolvesDefaultChatClients()
    {
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}");
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddZeeqLlm(Settings(keyRingPath), DevelopmentEnvironment());

        await using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<DefaultLlmChatClients>();

        await Assert.That(clients.Fast).IsNotNull();
        await Assert.That(clients.High).IsNotNull();
        await Assert.That(clients.Max).IsNotNull();
        await Assert.That(Directory.Exists(keyRingPath)).IsTrue();
    }

    [Test]
    public async Task AddZeeqLlm_RegistersBothSnippetEmbeddingGeneratorProfiles()
    {
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}");
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddZeeqLlm(
            Settings(keyRingPath) with
            {
                Embeddings = new LlmEmbeddingSettings { Enabled = true, ApiKey = "test-embed-key" },
            },
            DevelopmentEnvironment()
        );

        await using var provider = services.BuildServiceProvider();

        var batch = provider.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(
            DefaultLlmChatClientKeys.SnippetEmbeddingsBatch
        );
        var query = provider.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(
            DefaultLlmChatClientKeys.SnippetEmbeddingsQuery
        );

        await Assert.That(batch).IsNotNull();
        await Assert.That(query).IsNotNull();
    }

    [Test]
    public async Task AddZeeqLlm_WithEmbeddingsEnabledAndMissingApiKeyOutsideDevelopment_ThrowsConfigurationError()
    {
        var settings = Settings(
            Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}")
        ) with
        {
            Embeddings = new LlmEmbeddingSettings { Enabled = true, ApiKey = "" },
        };

        await Assert
            .That(() => new ServiceCollection().AddZeeqLlm(settings, ProductionEnvironment()))
            .Throws<InvalidOperationException>()
            .WithMessage(
                "AppSettings:Llm:Embeddings:ApiKey is required (and must not be left at its "
                    + "appsettings.json placeholder value) when AppSettings:Llm:Embeddings:Enabled "
                    + "is true outside Development."
            );
    }

    [Test]
    public async Task AddZeeqLlm_WithEmbeddingsEnabledAndMissingApiKeyInDevelopment_DoesNotThrow()
    {
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}");
        var settings = Settings(keyRingPath) with
        {
            Embeddings = new LlmEmbeddingSettings { Enabled = true, ApiKey = "" },
        };

        var services = new ServiceCollection();
        services.AddLogging();

        await Assert
            .That(() => services.AddZeeqLlm(settings, DevelopmentEnvironment()))
            .ThrowsNothing();
    }

    [Test]
    [Arguments("dotnet user-secrets set --project src/backend/Zeeq.Runtime.Server AppSettings:Llm:Embeddings:ApiKey secret-value")]
    [Arguments("gcloud secrets create AppSettings__Llm__Embeddings__ApiKey")]
    [Arguments("DOTNET user-secrets set")]
    [Arguments("GCLOUD secrets create")]
    public async Task AddZeeqLlm_WithPlaceholderEmbeddingsApiKeyOutsideDevelopment_ThrowsConfigurationError(
        string placeholderApiKey
    )
    {
        // Neither placeholder value is IsNullOrWhiteSpace, so this exercises the gap the old
        // empty-string-only check would have missed: a deploy that never bound the real secret,
        // leaving the appsettings.json reminder text in place.
        var settings = Settings(
            Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}")
        ) with
        {
            Embeddings = new LlmEmbeddingSettings { Enabled = true, ApiKey = placeholderApiKey },
        };

        await Assert
            .That(() => new ServiceCollection().AddZeeqLlm(settings, ProductionEnvironment()))
            .Throws<InvalidOperationException>()
            .WithMessage(
                "AppSettings:Llm:Embeddings:ApiKey is required (and must not be left at its "
                    + "appsettings.json placeholder value) when AppSettings:Llm:Embeddings:Enabled "
                    + "is true outside Development."
            );
    }

    [Test]
    public async Task AddZeeqLlm_WithPlaceholderEmbeddingsApiKeyInDevelopment_DoesNotThrow()
    {
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}");
        var settings = Settings(keyRingPath) with
        {
            Embeddings = new LlmEmbeddingSettings
            {
                Enabled = true,
                ApiKey = "gcloud secrets create AppSettings__Llm__Embeddings__ApiKey",
            },
        };

        var services = new ServiceCollection();
        services.AddLogging();

        await Assert
            .That(() => services.AddZeeqLlm(settings, DevelopmentEnvironment()))
            .ThrowsNothing();
    }

    [Test]
    public async Task DescribeEmbeddingConfiguration_WhenDisabled_ReturnsDisabled()
    {
        var settings = Settings("unused") with
        {
            Embeddings = new LlmEmbeddingSettings { Enabled = false },
        };

        await Assert
            .That(SetupLlm.DescribeEmbeddingConfiguration(settings))
            .IsEqualTo(LlmEmbeddingConfigurationStatus.Disabled);
    }

    [Test]
    public async Task DescribeEmbeddingConfiguration_WhenEnabledWithEmptyKey_ReturnsMissingApiKey()
    {
        var settings = Settings("unused") with
        {
            Embeddings = new LlmEmbeddingSettings { Enabled = true, ApiKey = "" },
        };

        await Assert
            .That(SetupLlm.DescribeEmbeddingConfiguration(settings))
            .IsEqualTo(LlmEmbeddingConfigurationStatus.MissingApiKey);
    }

    [Test]
    public async Task DescribeEmbeddingConfiguration_WhenEnabledWithPlaceholderKey_ReturnsPlaceholderApiKey()
    {
        var settings = Settings("unused") with
        {
            Embeddings = new LlmEmbeddingSettings
            {
                Enabled = true,
                ApiKey = "dotnet user-secrets set --project x y z",
            },
        };

        await Assert
            .That(SetupLlm.DescribeEmbeddingConfiguration(settings))
            .IsEqualTo(LlmEmbeddingConfigurationStatus.PlaceholderApiKey);
    }

    [Test]
    public async Task DescribeEmbeddingConfiguration_WhenEnabledWithRealKey_ReturnsConfigured()
    {
        var settings = Settings("unused") with
        {
            Embeddings = new LlmEmbeddingSettings { Enabled = true, ApiKey = "fw_real_key_123" },
        };

        await Assert
            .That(SetupLlm.DescribeEmbeddingConfiguration(settings))
            .IsEqualTo(LlmEmbeddingConfigurationStatus.Configured);
    }

    [Test]
    public async Task AddZeeqLlm_WithMissingFastApiKey_ThrowsConfigurationError()
    {
        var settings = Settings(
            Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}")
        ) with
        {
            Models = new LlmModelDefaults(),
        };

        await Assert
            .That(() => new ServiceCollection().AddZeeqLlm(settings, DevelopmentEnvironment()))
            .Throws<InvalidOperationException>()
            .WithMessage("AppSettings:Llm:Models:Fast:ApiKey is required.");
    }

    [Test]
    public async Task AddZeeqLlm_WithCloudKmsAndMissingGoogleKmsKeyName_ThrowsConfigurationError()
    {
        var settings = Settings(
            Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}")
        ) with
        {
            EncryptionProvider = LlmEncryptionProviders.CloudKms,
            GoogleKmsKeyName = "",
        };

        await Assert
            .That(() => new ServiceCollection().AddZeeqLlm(settings, DevelopmentEnvironment()))
            .Throws<InvalidOperationException>()
            .WithMessage("AppSettings:Llm:GoogleKmsKeyName is required for cloud-kms encryption.");
    }

    [Test]
    public async Task AddZeeqLlm_WithDevelopmentCloudKms_RegistersDataProtectionForLegacyRows()
    {
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"zeeq-llm-{Guid.NewGuid():N}");
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddZeeqLlm(
            Settings(keyRingPath) with
            {
                EncryptionProvider = LlmEncryptionProviders.CloudKms,
                GoogleKmsKeyName = "projects/test/locations/global/keyRings/test/cryptoKeys/key",
            },
            DevelopmentEnvironment()
        );

        await using var provider = services.BuildServiceProvider();
        var encryptionProviders = provider.GetRequiredService<
            IEnumerable<IDataEncryptionProvider>
        >();

        await Assert
            .That(encryptionProviders.Select(encryptionProvider => encryptionProvider.ProviderName))
            .Contains(LlmEncryptionProviders.DataProtection);
        await Assert.That(Directory.Exists(keyRingPath)).IsTrue();
    }

    private static LlmSettings Settings(string keyRingPath)
    {
        return new LlmSettings
        {
            DataProtectionKeyRingPath = keyRingPath,
            Models = new LlmModelDefaults
            {
                Fast = new LlmModelDefault
                {
                    ApiKey = "test-fast-key",
                    Model = "accounts/fireworks/models/glm-5p2",
                    Endpoint = "https://api.fireworks.ai/inference/v1",
                },
            },
        };
    }

    private static IHostEnvironment DevelopmentEnvironment()
    {
        return new TestHostEnvironment { EnvironmentName = Environments.Development };
    }

    private static IHostEnvironment ProductionEnvironment()
    {
        return new TestHostEnvironment { EnvironmentName = Environments.Production };
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Zeeq.Core.Llm.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
