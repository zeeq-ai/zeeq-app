using Zeeq.Core.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Core.Llm;

/// <summary>
/// Registers shared LLM settings, default clients, and local encryption services.
/// </summary>
public static class SetupLlm
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the shared LLM component to runtime composition.
        /// </summary>
        public IServiceCollection AddZeeqLlm(LlmSettings settings, IHostEnvironment environment)
        {
            Validate(settings, environment);

            services.AddSingleton(settings);
            services.AddMemoryCache();
            services.AddScoped<ILlmClientFactory, LlmClientFactory>();
            services.AddScoped<ILlmProviderAccessTester, LlmProviderAccessTester>();
            services.AddScoped<KeyEncryptionService>();
            services.AddSingleton(new LlmProviderAccessTestOptions());
            services.AddScoped<DefaultLlmChatClients>(serviceProvider => new DefaultLlmChatClients(
                serviceProvider.GetRequiredKeyedService<IChatClient>(DefaultLlmChatClientKeys.Fast),
                serviceProvider.GetRequiredKeyedService<IChatClient>(DefaultLlmChatClientKeys.High),
                serviceProvider.GetRequiredKeyedService<IChatClient>(DefaultLlmChatClientKeys.Max)
            ));

            AddDefaultChatClient(services, DefaultLlmChatClientKeys.Fast, settings.Models.Fast);
            AddDefaultChatClient(services, DefaultLlmChatClientKeys.High, settings.Models.High);
            AddDefaultChatClient(services, DefaultLlmChatClientKeys.Max, settings.Models.Max);

            AddDefaultEmbeddingGenerator(
                services,
                DefaultLlmChatClientKeys.SnippetEmbeddingsBatch,
                settings.Embeddings,
                EmbeddingClientProfile.Batch
            );
            AddDefaultEmbeddingGenerator(
                services,
                DefaultLlmChatClientKeys.SnippetEmbeddingsQuery,
                settings.Embeddings,
                EmbeddingClientProfile.Query
            );

            if (ShouldRegisterDataProtectionProvider(settings, environment))
            {
                Directory.CreateDirectory(settings.DataProtectionKeyRingPath);

                services
                    .AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtectionKeyRingPath));

                services.AddSingleton<IDataEncryptionProvider, DataProtectionEncryptionProvider>();
            }

            return services;
        }
    }

    private static void AddDefaultChatClient(
        IServiceCollection services,
        string key,
        LlmModelDefault model
    )
    {
        services.AddKeyedChatClient(
            key,
            serviceProvider =>
                serviceProvider
                    .GetRequiredService<ILlmClientFactory>()
                    .CreateDefaultChatClient(model),
            ServiceLifetime.Scoped
        );
    }

    private static void AddDefaultEmbeddingGenerator(
        IServiceCollection services,
        string key,
        LlmEmbeddingSettings embeddingSettings,
        EmbeddingClientProfile profile
    )
    {
        services.AddKeyedEmbeddingGenerator(
            key,
            serviceProvider =>
                serviceProvider
                    .GetRequiredService<ILlmClientFactory>()
                    .CreateDefaultEmbeddingGenerator(embeddingSettings, profile),
            ServiceLifetime.Scoped
        );
    }

    private static void Validate(LlmSettings settings, IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(settings.Models.Fast.ApiKey))
        {
            throw new InvalidOperationException("AppSettings:Llm:Models:Fast:ApiKey is required.");
        }

        if (
            settings.Embeddings.Enabled
            && !environment.IsDevelopment()
            && DescribeEmbeddingConfiguration(settings) != LlmEmbeddingConfigurationStatus.Configured
        )
        {
            // Covers both an empty key and a key that still holds its appsettings.json
            // placeholder value (e.g. "gcloud secrets create ..." — the literal reminder text
            // left behind when a deploy forgets to bind the real Secret Manager value). A
            // placeholder is not IsNullOrWhiteSpace, so without this it would silently pass
            // the old empty-string-only check and only fail once the first real embedding
            // call rejects it as an invalid API key.
            throw new InvalidOperationException(
                "AppSettings:Llm:Embeddings:ApiKey is required (and must not be left at its "
                    + "appsettings.json placeholder value) when AppSettings:Llm:Embeddings:Enabled "
                    + "is true outside Development."
            );
        }

        if (
            settings.EncryptionProvider.Equals(
                LlmEncryptionProviders.DataProtection,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "The data-protection LLM encryption provider is only allowed in Development."
                );
            }

            if (string.IsNullOrWhiteSpace(settings.DataProtectionKeyRingPath))
            {
                throw new InvalidOperationException(
                    "AppSettings:Llm:DataProtectionKeyRingPath is required for data-protection encryption."
                );
            }

            return;
        }

        if (
            settings.EncryptionProvider.Equals(
                LlmEncryptionProviders.CloudKms,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            if (string.IsNullOrWhiteSpace(settings.GoogleKmsKeyName))
            {
                throw new InvalidOperationException(
                    "AppSettings:Llm:GoogleKmsKeyName is required for cloud-kms encryption."
                );
            }

            return;
        }

        throw new InvalidOperationException(
            $"Unsupported LLM encryption provider '{settings.EncryptionProvider}'."
        );
    }

    private static bool ShouldRegisterDataProtectionProvider(
        LlmSettings settings,
        IHostEnvironment environment
    )
    {
        if (string.IsNullOrWhiteSpace(settings.DataProtectionKeyRingPath))
        {
            return false;
        }

        // NOTE: Production intentionally cannot use the data-protection provider; Validate rejects that
        // configuration before services are built. Development still registers it when a key-ring path
        // exists and cloud-kms is active so rows encrypted locally before switching can decrypt.
        return settings.EncryptionProvider.Equals(
                LlmEncryptionProviders.DataProtection,
                StringComparison.OrdinalIgnoreCase
            )
            || (
                environment.IsDevelopment()
                && settings.EncryptionProvider.Equals(
                    LlmEncryptionProviders.CloudKms,
                    StringComparison.OrdinalIgnoreCase
                )
            );
    }

    /// <summary>
    /// Classifies the snippet-embedding configuration for a clear, actionable startup log line.
    /// </summary>
    /// <remarks>
    /// Pure and side-effect free by design: <see cref="Zeeq.Core.Llm"/> stays logging-framework
    /// agnostic (it depends only on <c>Microsoft.Extensions.Logging</c> abstractions elsewhere,
    /// e.g. <see cref="LlmClientFactory"/>'s <c>ILoggerFactory</c>), so the actual log emission
    /// happens in the runtime host (<c>Program.cs</c>/<c>ZeeqWorkerHost.cs</c>), which already
    /// bootstraps Serilog before calling <c>AddZeeqLlm</c>. This method is the single source of
    /// truth both <see cref="Validate"/> (throws outside Development when not
    /// <see cref="LlmEmbeddingConfigurationStatus.Configured"/>) and the startup log call site
    /// read from, so the two can never disagree about what counts as "configured."
    /// </remarks>
    public static LlmEmbeddingConfigurationStatus DescribeEmbeddingConfiguration(LlmSettings settings)
    {
        if (!settings.Embeddings.Enabled)
        {
            return LlmEmbeddingConfigurationStatus.Disabled;
        }

        if (string.IsNullOrWhiteSpace(settings.Embeddings.ApiKey))
        {
            return LlmEmbeddingConfigurationStatus.MissingApiKey;
        }

        if (LooksLikeUnsetPlaceholder(settings.Embeddings.ApiKey))
        {
            return LlmEmbeddingConfigurationStatus.PlaceholderApiKey;
        }

        return LlmEmbeddingConfigurationStatus.Configured;
    }

    /// <summary>
    /// Detects the literal reminder text appsettings.Development.json/appsettings.Production.json
    /// ship in the <c>ApiKey</c> field before a real secret is bound — <c>"dotnet user-secrets
    /// set ..."</c> locally, <c>"gcloud secrets create ..."</c> in production. Neither is
    /// <see cref="string.IsNullOrWhiteSpace(string?)"/>, so without this check a deploy that
    /// forgot to bind the real secret would pass validation silently and only fail once the
    /// embedding pipeline sent the literal placeholder string to Fireworks as an API key.
    /// </summary>
    // NOTE: prefix matching (vs. an exact-value allowlist) is deliberate — the reminder text is a
    // shell command a developer can copy with different quoting/whitespace, so an allowlist of
    // exact strings would miss variants of the same unset placeholder (code review follow-up,
    // 2026-07-11).
    private static bool LooksLikeUnsetPlaceholder(string apiKey) =>
        apiKey.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase)
        || apiKey.StartsWith("gcloud", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The snippet-embedding configuration state <see cref="SetupLlm.DescribeEmbeddingConfiguration"/>
/// classifies at startup.
/// </summary>
public enum LlmEmbeddingConfigurationStatus
{
    /// <summary>
    /// <see cref="LlmEmbeddingSettings.Enabled"/> is false — snippet vectors are intentionally
    /// skipped; the full-text search arm remains fully functional either way.
    /// </summary>
    Disabled,

    /// <summary>Enabled, but <see cref="LlmEmbeddingSettings.ApiKey"/> is empty/whitespace.</summary>
    MissingApiKey,

    /// <summary>
    /// Enabled, but <see cref="LlmEmbeddingSettings.ApiKey"/> still holds its appsettings.json
    /// placeholder value — a deploy or local setup step that hasn't bound the real secret yet.
    /// </summary>
    PlaceholderApiKey,

    /// <summary>Enabled with what looks like a real API key.</summary>
    Configured,
}
