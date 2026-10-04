using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Zeeq.Core.Common;
using Zeeq.Core.Identity;
using Zeeq.Core.Security;

namespace Zeeq.Platform.Storage.Aws.Tests;

/// <summary>Verifies tenant isolation and configuration paths required by AWS installation.</summary>
public sealed class AwsDeploymentTests
{
    [Test]
    public async Task CopiedDatabaseSettingsUseTheNewIdentityForEveryFallback()
    {
        var original = new DatabaseSettings
        {
            ConnectionString = "Host=original;Username=original",
        };
        _ = original.EffectiveConnectionString;
        var copy = original with { ConnectionString = "Host=worker;Username=worker" };
        await Assert
            .That(new Npgsql.NpgsqlConnectionStringBuilder(copy.EffectiveConnectionString).Username)
            .IsEqualTo("worker");
        await Assert
            .That(
                new Npgsql.NpgsqlConnectionStringBuilder(
                    copy.EffectiveCacheConnectionString
                ).Username
            )
            .IsEqualTo("worker");
        await Assert
            .That(
                new Npgsql.NpgsqlConnectionStringBuilder(
                    copy.EffectiveWorkerConnectionString
                ).Username
            )
            .IsEqualTo("worker");
        await Assert
            .That(
                new Npgsql.NpgsqlConnectionStringBuilder(
                    original.EffectiveConnectionString
                ).Username
            )
            .IsEqualTo("original");
    }

    [Test]
    public async Task KmsRequiresOrganizationContextAndRejectsOversizedSecrets()
    {
        using var client = new FakeKmsClient();
        var provider = new AwsKmsEncryptionProvider(Settings(), client);
        var ciphertext = await provider.EncryptAsync(
            "org-one",
            Encoding.UTF8.GetBytes("secret"),
            CancellationToken.None
        );
        var clear = await provider.DecryptAsync("org-one", ciphertext, CancellationToken.None);
        await Assert.That(Encoding.UTF8.GetString(clear)).IsEqualTo("secret");
        await Assert
            .That(async () =>
            {
                await provider.DecryptAsync("org-two", ciphertext, CancellationToken.None);
            })
            .Throws<InvalidCiphertextException>();
        await Assert
            .That(async () =>
            {
                await provider.EncryptAsync("", new byte[1], CancellationToken.None);
            })
            .Throws<ArgumentException>();
        await Assert
            .That(async () =>
            {
                await provider.EncryptAsync("org", new byte[4097], CancellationToken.None);
            })
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(client.LastKey).IsEqualTo(Settings().AwsKmsKeyArn);
    }

    [Test]
    public async Task ReplacingTheConfiguredKmsKeyRejectsCiphertextFromThePreviousKey()
    {
        using var client = new FakeKmsClient();
        var original = new AwsKmsEncryptionProvider(Settings(), client);
        var ciphertext = await original.EncryptAsync(
            "org-one",
            Encoding.UTF8.GetBytes("secret"),
            CancellationToken.None
        );
        var replacement = new AwsKmsEncryptionProvider(
            Settings() with
            {
                AwsKmsKeyArn = "arn:aws:kms:us-east-2:123456789012:key/replacement",
            },
            client
        );

        await Assert
            .That(async () =>
            {
                await replacement.DecryptAsync("org-one", ciphertext, CancellationToken.None);
            })
            .Throws<IncorrectKeyException>();

        var replacementCiphertext = await replacement.EncryptAsync(
            "org-two",
            Encoding.UTF8.GetBytes("replacement secret"),
            CancellationToken.None
        );

        var clear = await original.DecryptAsync("org-one", ciphertext, CancellationToken.None);
        await Assert.That(Encoding.UTF8.GetString(clear)).IsEqualTo("secret");
        var replacementClear = await replacement.DecryptAsync(
            "org-two",
            replacementCiphertext,
            CancellationToken.None
        );
        await Assert
            .That(Encoding.UTF8.GetString(replacementClear))
            .IsEqualTo("replacement secret");
    }

    [Test]
    public async Task AwsSecurityRegistrationDoesNotRequireGoogleConfiguration()
    {
        var services = new ServiceCollection();
        services.AddZeeqSecurity(Settings(), new ProductionEnvironment());
        services.AddAwsKmsDataEncryption(Settings());
        // Client instantiation would access credentials; registration itself stays credential-independent.
        await Assert
            .That(services.Count(d => d.ServiceType == typeof(IDataEncryptionProvider)))
            .IsEqualTo(1);
    }

    [Test]
    public async Task ProductionCertificatesCanBeLoadedFromPayloadWithoutFiles()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=zeeq-test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1)
        );
        var payload = Convert.ToBase64String(
            certificate.Export(X509ContentType.Pfx, "test-password")
        );
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppSettings:Auth:OpenIddict:SigningCertificateBase64"] = payload,
                    ["AppSettings:Auth:OpenIddict:EncryptionCertificateBase64"] = payload,
                    ["AppSettings:Auth:OpenIddict:SigningCertificatePassword"] = "test-password",
                    ["AppSettings:Auth:OpenIddict:EncryptionCertificatePassword"] = "test-password",
                }
            )
            .Build();
        var provider = RuntimeSecretsProviderFactory.Create(config, new ProductionEnvironment());
        provider.ValidateStartup();
        await Assert.That(provider).IsTypeOf<ConfiguredCertificateRuntimeSecretsProvider>();
    }

    [Test]
    public async Task CertificatePayloadOverrideClearsTheInheritedFilePath()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:OpenIddict:SigningCertificatePath"] = "/unused/base.pfx",
                    ["AppSettings:Auth:OpenIddict:SigningCertificateBase64"] = "payload",
                }
            )
            .Build();
        var settings = OpenIddictCertificateSettings.Load(config);
        await Assert.That(settings.SigningCertificatePath).IsNull();
        await Assert.That(settings.SigningCertificateBase64).IsEqualTo("payload");
    }

    [Test]
    public async Task KmsDecryptionRemainsRegisteredAfterChangingTheActiveProvider()
    {
        var services = new ServiceCollection();
        services.AddAwsKmsDataEncryption(
            Settings() with
            {
                EncryptionProvider = DataEncryptionProviders.CloudKms,
            }
        );
        await Assert
            .That(services.Count(d => d.ServiceType == typeof(IDataEncryptionProvider)))
            .IsEqualTo(1);
    }

    private static SecuritySettings Settings() =>
        new()
        {
            EncryptionProvider = DataEncryptionProviders.AwsKms,
            AwsKmsKeyArn = "arn:aws:kms:us-east-2:123456789012:key/test",
            GoogleKmsKeyName = "",
            DataProtectionKeyRingPath = "",
        };

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeKmsClient()
        : AmazonKeyManagementServiceClient(
            new AnonymousAWSCredentials(),
            new AmazonKeyManagementServiceConfig { RegionEndpoint = Amazon.RegionEndpoint.USEast2 }
        )
    {
        private readonly Dictionary<
            string,
            (string Key, string Organization, byte[] Plaintext)
        > _issued = [];
        public string? LastKey { get; private set; }

        public override Task<EncryptResponse> EncryptAsync(
            EncryptRequest request,
            CancellationToken cancellationToken = default
        )
        {
            LastKey = request.KeyId;
            var ciphertext = RandomNumberGenerator.GetBytes(32);
            _issued[Convert.ToBase64String(ciphertext)] = (
                request.KeyId,
                request.EncryptionContext["organizationId"],
                request.Plaintext.ToArray()
            );
            return Task.FromResult(
                new EncryptResponse { CiphertextBlob = new MemoryStream(ciphertext) }
            );
        }

        public override Task<DecryptResponse> DecryptAsync(
            DecryptRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (
                !_issued.TryGetValue(
                    Convert.ToBase64String(request.CiphertextBlob.ToArray()),
                    out var issued
                )
            )
            {
                throw new InvalidCiphertextException("Unknown ciphertext.");
            }
            if (request.EncryptionContext["organizationId"] != issued.Organization)
            {
                throw new InvalidCiphertextException("Wrong organization.");
            }
            if (request.KeyId != issued.Key)
            {
                throw new IncorrectKeyException("Ciphertext belongs to a different key.");
            }
            LastKey = request.KeyId;
            return Task.FromResult(
                new DecryptResponse { Plaintext = new MemoryStream(issued.Plaintext) }
            );
        }
    }
}
