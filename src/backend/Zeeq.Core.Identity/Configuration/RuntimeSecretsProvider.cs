using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Core.Identity;

/// <summary>
/// Application boundary for OpenIddict signing and encryption material.
/// </summary>
/// <remarks>
/// OpenIddict needs stable signing and encryption material so tokens survive
/// process restarts and can be validated during key rollover. Development uses
/// local development certificates. Production must supply a real implementation
/// backed by the chosen secret/certificate store before the app can start.
/// </remarks>
public interface IRuntimeSecretsProvider
{
    /// <summary>
    /// Fails startup when the configured provider cannot supply production-safe material.
    /// </summary>
    void ValidateStartup();

    /// <summary>
    /// Registers OpenIddict signing and encryption material.
    /// </summary>
    void ConfigureOpenIddictServer(OpenIddictServerBuilder options);
}

/// <summary>
/// Builds the runtime secrets provider for the current hosting environment.
/// </summary>
public static class RuntimeSecretsProviderFactory
{
    /// <summary>
    /// Creates the runtime secrets provider appropriate for the current environment.
    /// </summary>
    public static IRuntimeSecretsProvider Create(
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        if (environment.IsDevelopment())
        {
            // Development uses file-backed certificates instead of OpenIddict's
            // keychain-based development certificates, which cannot retain their private
            // key on macOS and therefore regenerate on every restart (forcing DCR/MCP
            // clients to re-authenticate). See DevelopmentRuntimeSecretsProvider.
            var developmentSettings = OpenIddictCertificateSettings.Load(configuration);

            return new DevelopmentRuntimeSecretsProvider(
                developmentSettings.DevelopmentCertificatePath
            );
        }

        var settings = OpenIddictCertificateSettings.Load(configuration);

        if (settings.HasCompleteConfiguredCertificates)
        {
            return new ConfiguredCertificateRuntimeSecretsProvider(settings);
        }

        if (settings.HasAnyConfiguredCertificate)
        {
            // Partial production key material is almost always a deployment bug. Fail here with
            // an explicit config error instead of letting token setup fail later with a missing
            // signing/encryption certificate path.
            throw new InvalidOperationException(
                "OpenIddict certificate configuration is incomplete. Configure both "
                    + "Auth:OpenIddict:SigningCertificatePath and "
                    + "Auth:OpenIddict:EncryptionCertificatePath."
            );
        }

        return new MissingProductionRuntimeSecretsProvider(environment.EnvironmentName);
    }
}

/// <summary>
/// Configures production OpenIddict signing and encryption certificates.
/// </summary>
/// <remarks>
/// Values are read from <c>Auth:OpenIddict</c> and can be overlaid by
/// <c>AppSettings:Auth:OpenIddict</c> so secret values can come from user
/// secrets, environment variables, Aspire parameters, or mounted secret files.
///
/// Create local bootstrap certificates with:
/// <code>
/// mkdir -p .secrets/openiddict
/// openssl req -x509 -newkey rsa:4096 -sha256 -days 825 -nodes -subj "/CN=mcp-openiddict signing" -keyout .secrets/openiddict/signing.key -out .secrets/openiddict/signing.crt
/// openssl pkcs12 -export -inkey .secrets/openiddict/signing.key -in .secrets/openiddict/signing.crt -out .secrets/openiddict/signing.pfx
/// openssl req -x509 -newkey rsa:4096 -sha256 -days 825 -nodes -subj "/CN=mcp-openiddict encryption" -keyout .secrets/openiddict/encryption.key -out .secrets/openiddict/encryption.crt
/// openssl pkcs12 -export -inkey .secrets/openiddict/encryption.key -in .secrets/openiddict/encryption.crt -out .secrets/openiddict/encryption.pfx
/// </code>
///
/// Configure with:
/// <code>
/// Auth:OpenIddict:SigningCertificatePath=/run/secrets/openiddict/signing.pfx
/// Auth:OpenIddict:SigningCertificatePassword=...
/// Auth:OpenIddict:EncryptionCertificatePath=/run/secrets/openiddict/encryption.pfx
/// Auth:OpenIddict:EncryptionCertificatePassword=...
/// </code>
/// </remarks>
public sealed class OpenIddictCertificateSettings
{
    /// <summary>
    /// Path to the PFX certificate used to sign OpenIddict tokens.
    /// </summary>
    public string? SigningCertificatePath { get; set; }

    /// <summary>
    /// Password for <see cref="SigningCertificatePath"/>, when required.
    /// </summary>
    public string? SigningCertificatePassword { get; set; }

    /// <summary>
    /// Path to the PFX certificate used to encrypt OpenIddict tokens.
    /// </summary>
    public string? EncryptionCertificatePath { get; set; }

    /// <summary>
    /// Password for <see cref="EncryptionCertificatePath"/>, when required.
    /// </summary>
    public string? EncryptionCertificatePassword { get; set; }

    /// <summary>
    /// Whether any signing or encryption certificate path was configured.
    /// </summary>
    public bool HasAnyConfiguredCertificate =>
        !string.IsNullOrWhiteSpace(SigningCertificatePath)
        || !string.IsNullOrWhiteSpace(EncryptionCertificatePath);

    /// <summary>
    /// Whether both signing and encryption certificate paths were configured.
    /// </summary>
    public bool HasCompleteConfiguredCertificates =>
        !string.IsNullOrWhiteSpace(SigningCertificatePath)
        && !string.IsNullOrWhiteSpace(EncryptionCertificatePath);

    /// <summary>
    /// Directory used to persist self-signed development signing/encryption
    /// certificates so OpenIddict tokens survive local server restarts.
    /// </summary>
    /// <remarks>
    /// Resolved relative to the server process working directory
    /// (<c>src/backend/Zeeq.Runtime.Server</c> under Aspire), placing it beside the
    /// Data Protection key ring at <c>.secrets/data-protection-keys</c>. The
    /// directory is gitignored. Only consumed by
    /// <see cref="DevelopmentRuntimeSecretsProvider"/>; production uses the explicit
    /// PFX paths above.
    /// </remarks>
    public string DevelopmentCertificatePath { get; set; } = ".secrets/local-certs";

    /// <summary>
    /// Loads certificate settings from normal configuration and app-settings overlays.
    /// </summary>
    public static OpenIddictCertificateSettings Load(IConfiguration configuration)
    {
        var settings =
            configuration.GetSection("Auth:OpenIddict").Get<OpenIddictCertificateSettings>()
            ?? new();
        var secretSettings =
            configuration
                .GetSection("AppSettings:Auth:OpenIddict")
                .Get<OpenIddictCertificateSettings>()
            ?? new();

        Overlay(settings, secretSettings);

        return settings;

        static void Overlay(
            OpenIddictCertificateSettings target,
            OpenIddictCertificateSettings source
        )
        {
            if (!string.IsNullOrWhiteSpace(source.SigningCertificatePath))
            {
                target.SigningCertificatePath = source.SigningCertificatePath;
            }

            if (!string.IsNullOrWhiteSpace(source.SigningCertificatePassword))
            {
                target.SigningCertificatePassword = source.SigningCertificatePassword;
            }

            if (!string.IsNullOrWhiteSpace(source.EncryptionCertificatePath))
            {
                target.EncryptionCertificatePath = source.EncryptionCertificatePath;
            }

            if (!string.IsNullOrWhiteSpace(source.EncryptionCertificatePassword))
            {
                target.EncryptionCertificatePassword = source.EncryptionCertificatePassword;
            }
        }
    }
}

/// <summary>
/// Supplies stable, file-backed OpenIddict signing and encryption certificates
/// for local development.
/// </summary>
/// <remarks>
/// OpenIddict's built-in <c>AddDevelopmentSigningCertificate</c>/
/// <c>AddDevelopmentEncryptionCertificate</c> persist the certificate to the
/// X.509 <c>CurrentUser/My</c> store. On macOS that store is the login keychain,
/// which cannot retain the RSA private key in a retrievable form, so every
/// process restart silently regenerates the key material. Tokens sealed before
/// the restart (including refresh tokens held by DCR/MCP clients such as Cursor)
/// then fail to decrypt and force a full re-authentication.
///
/// This provider instead generates self-signed certificates once, persists them
/// as password-less PFX files under <see cref="_certificateDirectory"/> (default
/// <c>.secrets/local-certs</c>, gitignored), and reloads them with
/// <see cref="X509KeyStorageFlags.DefaultKeySet"/> on every run. Because the
/// private key is loaded from the file-backed PFX (never imported into the
/// keychain), it remains usable across restarts on macOS, so the
/// same material survives restarts for the life of the PFX files. Delete the
/// directory to rotate (invalidates existing dev tokens). Production uses
/// <see cref="ConfiguredCertificateRuntimeSecretsProvider"/>.
/// </remarks>
public sealed class DevelopmentRuntimeSecretsProvider : IRuntimeSecretsProvider
{
    private const string SigningCertificateFileName = "signing.pfx";
    private const string EncryptionCertificateFileName = "encryption.pfx";
    private const string SigningSubject = "CN=Zeeq Development Signing Certificate";
    private const string EncryptionSubject = "CN=Zeeq Development Encryption Certificate";

    private readonly string _certificateDirectory;

    /// <summary>
    /// Initializes the provider and ensures the certificate directory exists.
    /// </summary>
    /// <param name="certificateDirectory">Directory where PFX files are persisted.</param>
    public DevelopmentRuntimeSecretsProvider(string certificateDirectory)
    {
        _certificateDirectory = certificateDirectory;
        Directory.CreateDirectory(certificateDirectory);
    }

    /// <inheritdoc />
    public void ValidateStartup() { }

    /// <inheritdoc />
    public void ConfigureOpenIddictServer(OpenIddictServerBuilder options)
    {
        var signingCertificate = LoadOrCreateCertificate(
            fileName: SigningCertificateFileName,
            subjectName: SigningSubject,
            keyUsages: X509KeyUsageFlags.DigitalSignature
        );
        var encryptionCertificate = LoadOrCreateCertificate(
            fileName: EncryptionCertificateFileName,
            subjectName: EncryptionSubject,
            keyUsages: X509KeyUsageFlags.KeyEncipherment
        );

        options.AddSigningCertificate(signingCertificate);
        options.AddEncryptionCertificate(encryptionCertificate);
    }

    /// <summary>
    /// Loads a persisted development certificate, generating and persisting one on
    /// first use.
    /// </summary>
    /// <remarks>
    /// The certificate always loads from the file path (never from in-memory bytes
    /// a process just generated) so that every process — including the split-mode
    /// <c>zeeq-worker</c> started concurrently — converges on the same persisted
    /// key material. Loading with <see cref="X509KeyStorageFlags.DefaultKeySet"/>
    /// which loads the private key from the file data without importing into the
    /// macOS keychain, making password-less PFX files usable on macOS.
    /// </remarks>
    /// <param name="fileName">PFX file name within the certificate directory.</param>
    /// <param name="subjectName">Distinguished name, e.g. <c>CN=Zeeq Development Signing Certificate</c>.</param>
    /// <param name="keyUsages">Key usage flags: signing vs. key encipherment.</param>
    /// <returns>A certificate with a usable private key for the process lifetime.</returns>
    private X509Certificate2 LoadOrCreateCertificate(
        string fileName,
        string subjectName,
        X509KeyUsageFlags keyUsages
    )
    {
        var path = Path.Combine(_certificateDirectory, fileName);

        if (!File.Exists(path))
        {
            CreateAndPersistCertificate(path, subjectName, keyUsages);
        }

        return X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password: null,
            keyStorageFlags: X509KeyStorageFlags.DefaultKeySet
        );
    }

    /// <summary>
    /// Generates a self-signed certificate and atomically persists it as a
    /// password-less PFX.
    /// </summary>
    /// <remarks>
    /// Validity is backdated one day so brief clock skew across local services
    /// never rejects a freshly minted certificate. The write goes to a unique temp
    /// file that is moved into place without overwrite; if another process
    /// (split-mode worker) created the file first, the temp copy is discarded and
    /// the caller loads the winner's file.
    /// </remarks>
    private static void CreateAndPersistCertificate(
        string path,
        string subjectName,
        X509KeyUsageFlags keyUsages
    )
    {
        using var rsa = RSA.Create(2048);

        var request = new CertificateRequest(
            subjectName,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsages, critical: false));

        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2)
        );

        var pfxBytes = generated.Export(X509ContentType.Pfx);

        var directory = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllBytes(tempPath, pfxBytes);

        try
        {
            File.Move(tempPath, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process generated the certificate first; discard ours and let
            // the caller load the winning file so both processes converge.
            File.Delete(tempPath);
        }
    }
}

/// <summary>
/// Loads configured PFX certificates for production OpenIddict tokens.
/// </summary>
/// <remarks>
/// The signing certificate's private key stays with the auth server. Upstream
/// services, such as an OpenTelemetry collector, validate signed JWT access
/// tokens using the public key published by the OpenIddict JWKS endpoint.
/// </remarks>
public sealed class ConfiguredCertificateRuntimeSecretsProvider(
    OpenIddictCertificateSettings settings
) : IRuntimeSecretsProvider
{
    /// <inheritdoc />
    public void ValidateStartup()
    {
        _ = LoadCertificate(
            settings.SigningCertificatePath,
            settings.SigningCertificatePassword,
            "signing"
        );
        _ = LoadCertificate(
            settings.EncryptionCertificatePath,
            settings.EncryptionCertificatePassword,
            "encryption"
        );
    }

    /// <inheritdoc />
    public void ConfigureOpenIddictServer(OpenIddictServerBuilder options)
    {
        var signingCertificate = LoadCertificate(
            settings.SigningCertificatePath,
            settings.SigningCertificatePassword,
            "signing"
        );
        var encryptionCertificate = LoadCertificate(
            settings.EncryptionCertificatePath,
            settings.EncryptionCertificatePassword,
            "encryption"
        );

        options.AddSigningCertificate(signingCertificate);
        options.AddEncryptionCertificate(encryptionCertificate);
    }

    private static X509Certificate2 LoadCertificate(string? path, string? password, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"OpenIddict {purpose} certificate path is not configured."
            );
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"OpenIddict {purpose} certificate file does not exist: {path}"
            );
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.DefaultKeySet
        );

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"OpenIddict {purpose} certificate must include a private key."
            );
        }

        return certificate;
    }
}

/// <summary>
/// Explicit fail-closed provider used until production key material is configured.
/// </summary>
/// <remarks>
/// Replace this with a provider that loads signing/encryption certificates or
/// keys from the selected managed secret store. The production implementation
/// should document rollover timing and keep old validation material available
/// until all issued tokens signed or encrypted with that material expire.
/// </remarks>
public sealed class MissingProductionRuntimeSecretsProvider(string environmentName)
    : IRuntimeSecretsProvider
{
    /// <inheritdoc />
    public void ValidateStartup()
    {
        throw new InvalidOperationException(
            $"OpenIddict runtime signing/encryption material is not configured for environment '{environmentName}'. "
                + "Configure a production IRuntimeSecretsProvider before starting outside Development."
        );
    }

    /// <inheritdoc />
    public void ConfigureOpenIddictServer(OpenIddictServerBuilder options)
    {
        ValidateStartup();
    }
}
