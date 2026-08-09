using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Core.Security;

/// <summary>
/// Registers provider-neutral encrypted-value operations.
/// </summary>
/// <remarks>
/// Extracted from <c>Zeeq.Core.Llm.SetupLlm</c> (zeeq-ai/zeeq-app#192): the runtime host now
/// calls this before <c>AddZeeqLlm</c>/<c>AddGoogleKmsDataEncryption</c> so both can depend on
/// <see cref="EncryptedValueEncryptionService"/> without pulling in the LLM composition unit.
/// </remarks>
public static class SetupSecurity
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds encrypted-value storage services and the local Data Protection provider when
        /// configured.
        /// </summary>
        public IServiceCollection AddZeeqSecurity(
            SecuritySettings settings,
            IHostEnvironment environment
        )
        {
            Validate(settings, environment);

            services.AddSingleton(settings);
            services.AddScoped<EncryptedValueEncryptionService>();

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

    private static void Validate(SecuritySettings settings, IHostEnvironment environment)
    {
        if (
            settings.EncryptionProvider.Equals(
                DataEncryptionProviders.DataProtection,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "The data-protection encryption provider is only allowed in Development."
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
                DataEncryptionProviders.CloudKms,
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
            $"Unsupported data encryption provider '{settings.EncryptionProvider}'."
        );
    }

    private static bool ShouldRegisterDataProtectionProvider(
        SecuritySettings settings,
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
                DataEncryptionProviders.DataProtection,
                StringComparison.OrdinalIgnoreCase
            )
            || (
                environment.IsDevelopment()
                && settings.EncryptionProvider.Equals(
                    DataEncryptionProviders.CloudKms,
                    StringComparison.OrdinalIgnoreCase
                )
            );
    }
}
