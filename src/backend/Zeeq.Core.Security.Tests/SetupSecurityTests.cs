using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Core.Security.Tests;

/// <summary>
/// Unit tests for encrypted-value service registration and startup validation.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Core.Security.Tests --output detailed --disable-logo --treenode-filter "/*/*/SetupSecurityTests/*"
/// </summary>
public sealed class SetupSecurityTests
{
    [Test]
    public async Task AddZeeqSecurity_WithCloudKmsAndMissingGoogleKmsKeyName_ThrowsConfigurationError()
    {
        var settings = Settings(
            Path.Combine(Path.GetTempPath(), $"zeeq-security-{Guid.NewGuid():N}")
        ) with
        {
            EncryptionProvider = DataEncryptionProviders.CloudKms,
            GoogleKmsKeyName = "",
        };

        await Assert
            .That(() => new ServiceCollection().AddZeeqSecurity(settings, DevelopmentEnvironment()))
            .Throws<InvalidOperationException>()
            .WithMessage("AppSettings:Llm:GoogleKmsKeyName is required for cloud-kms encryption.");
    }

    [Test]
    public async Task AddZeeqSecurity_WithDevelopmentCloudKms_RegistersDataProtectionForLegacyRows()
    {
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"zeeq-security-{Guid.NewGuid():N}");
        var services = new ServiceCollection();

        services.AddZeeqSecurity(
            Settings(keyRingPath) with
            {
                EncryptionProvider = DataEncryptionProviders.CloudKms,
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
            .Contains(DataEncryptionProviders.DataProtection);
        await Assert.That(Directory.Exists(keyRingPath)).IsTrue();
    }

    [Test]
    public async Task AddZeeqSecurity_WithDataProtectionOutsideDevelopment_ThrowsConfigurationError()
    {
        var settings = Settings(
            Path.Combine(Path.GetTempPath(), $"zeeq-security-{Guid.NewGuid():N}")
        );

        await Assert
            .That(() => new ServiceCollection().AddZeeqSecurity(settings, ProductionEnvironment()))
            .Throws<InvalidOperationException>()
            .WithMessage("The data-protection encryption provider is only allowed in Development.");
    }

    [Test]
    public async Task AddZeeqSecurity_WithDataProtectionInDevelopment_CreatesKeyRingDirectory()
    {
        var keyRingPath = Path.Combine(Path.GetTempPath(), $"zeeq-security-{Guid.NewGuid():N}");
        var services = new ServiceCollection();

        services.AddZeeqSecurity(Settings(keyRingPath), DevelopmentEnvironment());

        await using var provider = services.BuildServiceProvider();
        var encryptionService = provider.GetRequiredService<EncryptedValueEncryptionService>();

        await Assert.That(encryptionService).IsNotNull();
        await Assert.That(Directory.Exists(keyRingPath)).IsTrue();
    }

    private static SecuritySettings Settings(string keyRingPath) =>
        new()
        {
            EncryptionProvider = DataEncryptionProviders.DataProtection,
            DataProtectionKeyRingPath = keyRingPath,
            GoogleKmsKeyName = string.Empty,
        };

    private static IHostEnvironment DevelopmentEnvironment() =>
        new TestHostEnvironment { EnvironmentName = Environments.Development };

    private static IHostEnvironment ProductionEnvironment() =>
        new TestHostEnvironment { EnvironmentName = Environments.Production };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Zeeq.Core.Security.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
