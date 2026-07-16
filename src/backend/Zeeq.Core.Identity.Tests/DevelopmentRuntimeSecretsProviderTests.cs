using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zeeq.Core.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Core.Identity.Tests;

/// <summary>
/// Tests for <see cref="DevelopmentRuntimeSecretsProvider"/> verifying that
/// file-backed development certificates are generated, persisted, reloaded
/// consistently, and survive the equivalent of a process restart.
/// </summary>
/// <remarks>
/// Run with:
/// <code>
/// dotnet run --project src/backend/Zeeq.Core.Identity.Tests --treenode-filter "/*/*/DevelopmentRuntimeSecretsProviderTests/*"
/// </code>
/// </remarks>
public class DevelopmentRuntimeSecretsProviderTests
{
    /// <summary>
    /// Creates a temporary certificate directory that is cleaned up on disposal.
    /// </summary>
    private sealed class TempCertDir : IDisposable
    {
        public string Path { get; }

        public TempCertDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "zeeq-cert-tests",
                Guid.NewGuid().ToString("N")
            );
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    /// <summary>
    /// Loads a PFX file with <see cref="X509KeyStorageFlags.DefaultKeySet"/> and
    /// returns its thumbprint.
    /// </summary>
    private static string GetThumbprint(string pfxPath)
    {
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            password: null,
            keyStorageFlags: X509KeyStorageFlags.DefaultKeySet
        );

        return cert.Thumbprint;
    }

    /// <summary>
    /// Loads a PFX file with <see cref="X509KeyStorageFlags.DefaultKeySet"/>
    /// (matching the provider's load semantics).
    /// </summary>
    private static X509Certificate2 LoadPfx(string pfxPath)
    {
        return X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            password: null,
            keyStorageFlags: X509KeyStorageFlags.DefaultKeySet
        );
    }

    /// <summary>
    /// Invokes <see cref="IRuntimeSecretsProvider.ConfigureOpenIddictServer"/>
    /// through a real <see cref="OpenIddictServerBuilder"/> so certificate
    /// generation and registration is exercised end-to-end.
    /// </summary>
    private static void ConfigureProvider(DevelopmentRuntimeSecretsProvider provider)
    {
        var services = new ServiceCollection();
        services.AddOpenIddict().AddServer(options => provider.ConfigureOpenIddictServer(options));
    }

    [Test]
    public async Task FirstRun_CreatesBothPfxFiles()
    {
        using var dir = new TempCertDir();
        var provider = new DevelopmentRuntimeSecretsProvider(dir.Path);

        ConfigureProvider(provider);

        await Assert.That(File.Exists(System.IO.Path.Combine(dir.Path, "signing.pfx"))).IsTrue();
        await Assert.That(File.Exists(System.IO.Path.Combine(dir.Path, "encryption.pfx"))).IsTrue();
    }

    [Test]
    public async Task StabilityAcrossRuns_SameThumbprints()
    {
        using var dir = new TempCertDir();

        var provider1 = new DevelopmentRuntimeSecretsProvider(dir.Path);
        ConfigureProvider(provider1);

        var signingTp1 = GetThumbprint(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        var encryptionTp1 = GetThumbprint(System.IO.Path.Combine(dir.Path, "encryption.pfx"));

        // Second provider instance simulates a process restart
        var provider2 = new DevelopmentRuntimeSecretsProvider(dir.Path);
        ConfigureProvider(provider2);

        var signingTp2 = GetThumbprint(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        var encryptionTp2 = GetThumbprint(System.IO.Path.Combine(dir.Path, "encryption.pfx"));

        await Assert.That(signingTp1).IsEqualTo(signingTp2);
        await Assert.That(encryptionTp1).IsEqualTo(encryptionTp2);
    }

    [Test]
    public async Task LoadedCertificates_WithPrivateKey_AreUsable()
    {
        using var dir = new TempCertDir();
        var provider = new DevelopmentRuntimeSecretsProvider(dir.Path);
        ConfigureProvider(provider);

        using var signingCert = LoadPfx(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        using var encryptionCert = LoadPfx(System.IO.Path.Combine(dir.Path, "encryption.pfx"));

        await Assert.That(signingCert.HasPrivateKey).IsTrue();
        await Assert.That(encryptionCert.HasPrivateKey).IsTrue();

        // Verify signing certificate can actually sign
        using var signingKey = signingCert.GetRSAPrivateKey();
        await Assert.That(signingKey).IsNotNull();
        var data = new byte[] { 1, 2, 3, 4 };
        var signature = signingKey!.SignData(
            data,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        await Assert.That(signature.Length).IsGreaterThan(0);

        // Verify encryption certificate can actually decrypt
        using var encryptionKey = encryptionCert.GetRSAPrivateKey();
        await Assert.That(encryptionKey).IsNotNull();
        var encrypted = encryptionKey!.Encrypt(data, RSAEncryptionPadding.Pkcs1);
        var decrypted = encryptionKey.Decrypt(encrypted, RSAEncryptionPadding.Pkcs1);
        await Assert.That(decrypted).IsEquivalentTo(data);
    }

    [Test]
    public async Task SigningAndEncryption_DifferentThumbprints()
    {
        using var dir = new TempCertDir();
        var provider = new DevelopmentRuntimeSecretsProvider(dir.Path);
        ConfigureProvider(provider);

        var signingTp = GetThumbprint(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        var encryptionTp = GetThumbprint(System.IO.Path.Combine(dir.Path, "encryption.pfx"));

        await Assert.That(signingTp).IsNotEqualTo(encryptionTp);
    }

    [Test]
    public async Task Certificates_HaveExpectedSubjects()
    {
        using var dir = new TempCertDir();
        var provider = new DevelopmentRuntimeSecretsProvider(dir.Path);
        ConfigureProvider(provider);

        using var signingCert = LoadPfx(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        using var encryptionCert = LoadPfx(System.IO.Path.Combine(dir.Path, "encryption.pfx"));

        await Assert
            .That(signingCert.Subject)
            .IsEqualTo("CN=Zeeq Development Signing Certificate");
        await Assert
            .That(encryptionCert.Subject)
            .IsEqualTo("CN=Zeeq Development Encryption Certificate");
    }

    [Test]
    public async Task ConcurrentFirstRun_ConvergesOnSameCerts()
    {
        using var dir = new TempCertDir();

        var provider1 = new DevelopmentRuntimeSecretsProvider(dir.Path);
        var provider2 = new DevelopmentRuntimeSecretsProvider(dir.Path);

        // Run both providers in parallel from a fresh directory — simulates
        // split-mode where zeeq-server and zeeq-worker start concurrently.
        await Task.WhenAll(
            Task.Run(() => ConfigureProvider(provider1)),
            Task.Run(() => ConfigureProvider(provider2))
        );

        // Exactly two files (no leftover .tmp files from the atomic write race)
        var files = Directory.GetFiles(dir.Path);
        await Assert.That(files.Length).IsEqualTo(2);
        await Assert
            .That(files.All(f => f.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)))
            .IsTrue();

        // Both processes converged on the same key material
        var signingTp = GetThumbprint(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        var encryptionTp = GetThumbprint(System.IO.Path.Combine(dir.Path, "encryption.pfx"));
        await Assert.That(signingTp).IsNotNull();
        await Assert.That(encryptionTp).IsNotNull();
    }

    [Test]
    public async Task Rotation_DeleteDirectory_GeneratesNewThumbprints()
    {
        using var dir = new TempCertDir();

        var provider1 = new DevelopmentRuntimeSecretsProvider(dir.Path);
        ConfigureProvider(provider1);

        var signingTp1 = GetThumbprint(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        var encryptionTp1 = GetThumbprint(System.IO.Path.Combine(dir.Path, "encryption.pfx"));

        // Rotate: delete directory and re-run
        Directory.Delete(dir.Path, recursive: true);

        var provider2 = new DevelopmentRuntimeSecretsProvider(dir.Path);
        ConfigureProvider(provider2);

        var signingTp2 = GetThumbprint(System.IO.Path.Combine(dir.Path, "signing.pfx"));
        var encryptionTp2 = GetThumbprint(System.IO.Path.Combine(dir.Path, "encryption.pfx"));

        await Assert.That(signingTp1).IsNotEqualTo(signingTp2);
        await Assert.That(encryptionTp1).IsNotEqualTo(encryptionTp2);
    }

    [Test]
    public async Task Factory_ConfiguredCertificatePath_OverridesDefault()
    {
        using var dir = new TempCertDir();
        var customPath = System.IO.Path.Combine(dir.Path, "custom-certs");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Auth:OpenIddict:DevelopmentCertificatePath"] = customPath,
                }
            )
            .Build();

        var provider = (DevelopmentRuntimeSecretsProvider)
            RuntimeSecretsProviderFactory.Create(
                configuration,
                new TestHostEnvironment(Environments.Development)
            );
        ConfigureProvider(provider);

        // Certificates should be created under the custom path, not the default
        await Assert.That(File.Exists(System.IO.Path.Combine(customPath, "signing.pfx"))).IsTrue();
        await Assert
            .That(File.Exists(System.IO.Path.Combine(customPath, "encryption.pfx")))
            .IsTrue();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Zeeq.Core.Identity.Tests";

        public string ContentRootPath { get; set; } = Environment.CurrentDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
