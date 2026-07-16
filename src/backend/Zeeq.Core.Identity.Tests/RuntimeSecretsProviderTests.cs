using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zeeq.Core.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Core.Identity.Tests;

public class RuntimeSecretsProviderTests
{
    [Test]
    public async Task Create_WithDevelopmentEnvironment_ReturnsDevelopmentProvider()
    {
        var provider = RuntimeSecretsProviderFactory.Create(
            new ConfigurationBuilder().Build(),
            new TestHostEnvironment(Environments.Development)
        );

        await Assert.That(provider.GetType()).IsEqualTo(typeof(DevelopmentRuntimeSecretsProvider));
    }

    [Test]
    public async Task ValidateStartup_WithProductionEnvironment_ThrowsWhenSecretsAreMissing()
    {
        var provider = RuntimeSecretsProviderFactory.Create(
            new ConfigurationBuilder().Build(),
            new TestHostEnvironment(Environments.Production)
        );

        InvalidOperationException? exception = null;
        try
        {
            provider.ValidateStartup();
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        await Assert.That(exception).IsNotNull();
        await Assert
            .That(exception!.Message.Contains("runtime signing/encryption material"))
            .IsTrue();
    }

    [Test]
    public async Task Create_WithProductionCertificateSettings_ReturnsConfiguredProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Auth:OpenIddict:SigningCertificatePath"] = "/tmp/signing.pfx",
                    ["AppSettings:Auth:OpenIddict:EncryptionCertificatePath"] =
                        "/tmp/encryption.pfx",
                }
            )
            .Build();

        var provider = RuntimeSecretsProviderFactory.Create(
            configuration,
            new TestHostEnvironment(Environments.Production)
        );

        await Assert
            .That(provider.GetType())
            .IsEqualTo(typeof(ConfiguredCertificateRuntimeSecretsProvider));
    }

    [Test]
    public async Task Create_WithPartialProductionCertificateSettings_ThrowsIncompleteConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Auth:OpenIddict:SigningCertificatePath"] = "/tmp/signing.pfx",
                }
            )
            .Build();

        InvalidOperationException? exception = null;
        try
        {
            _ = RuntimeSecretsProviderFactory.Create(
                configuration,
                new TestHostEnvironment(Environments.Production)
            );
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message.Contains("configuration is incomplete")).IsTrue();
    }

    [Test]
    public async Task Load_WithAppSettingsOverlay_UsesSecretValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:OpenIddict:SigningCertificatePath"] = "/base/signing.pfx",
                    ["AppSettings:Auth:OpenIddict:SigningCertificatePath"] = "/secrets/signing.pfx",
                    ["AppSettings:Auth:OpenIddict:SigningCertificatePassword"] = "secret",
                }
            )
            .Build();

        var settings = OpenIddictCertificateSettings.Load(configuration);

        await Assert.That(settings.SigningCertificatePath).IsEqualTo("/secrets/signing.pfx");
        await Assert.That(settings.SigningCertificatePassword).IsEqualTo("secret");
    }

    [Test]
    public async Task ValidateStartup_WithConfiguredCertificates_LoadsPfxFiles()
    {
        using var tempDirectory = new TemporaryCertificateDirectory();
        var signingPath = tempDirectory.CreateCertificate("signing");
        var encryptionPath = tempDirectory.CreateCertificate("encryption");
        var settings = new OpenIddictCertificateSettings
        {
            SigningCertificatePath = signingPath,
            SigningCertificatePassword = TemporaryCertificateDirectory.Password,
            EncryptionCertificatePath = encryptionPath,
            EncryptionCertificatePassword = TemporaryCertificateDirectory.Password,
        };

        var provider = new ConfiguredCertificateRuntimeSecretsProvider(settings);

        provider.ValidateStartup();

        await Assert.That(File.Exists(signingPath)).IsTrue();
        await Assert.That(File.Exists(encryptionPath)).IsTrue();
    }

    private sealed class TemporaryCertificateDirectory : IDisposable
    {
        public const string Password = "test-password";

        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "zeeq-identity-tests-" + Guid.NewGuid().ToString("N")
        );

        public TemporaryCertificateDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string CreateCertificate(string name)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN=zeeq {name}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1)
            );
            var path = Path.Combine(_path, name + ".pfx");

            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, Password));

            return path;
        }

        public void Dispose()
        {
            Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Zeeq.Core.Identity.Tests";

        public string ContentRootPath { get; set; } = Environment.CurrentDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
