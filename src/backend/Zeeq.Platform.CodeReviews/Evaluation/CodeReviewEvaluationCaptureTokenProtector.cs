using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Creates and validates short-lived capture tokens that authenticate a staged dataset save.
/// </summary>
/// <remarks>
/// Capture is expensive and must happen outside the dataset transaction, so the token is the
/// only carrier of the staged result. It is AES-GCM encrypted with a purpose-derived key, like
/// the existing diff-upload token, and it binds the organization, repository, dataset,
/// expected dataset version, pull request number and artifact identity. The payload stays
/// small because the snapshot bytes live in artifact storage, not in the token.
///
/// A successful save increments the dataset version, so replaying the same token fails the
/// version check without any staging table or nonce registry. Expiry uses the injected
/// <see cref="TimeProvider"/> so tests never sleep.
/// </remarks>
public sealed class CodeReviewEvaluationCaptureTokenProtector(
    CodeReviewSettings settings,
    TimeProvider timeProvider
)
{
    private const byte Version = 1;
    private const string Purpose = "Zeeq.CodeReview.EvaluationCapture.v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // Protected token byte layout before Base64Url encoding:
    // [0]                         = token format version
    // [1..12]                     = AES-GCM nonce
    // [13..28]                    = AES-GCM authentication tag
    // [29..end]                   = encrypted JSON payload
    private const int VersionIndex = 0;
    private const int NonceStartIndex = VersionIndex + 1;
    private const int TagStartIndex = NonceStartIndex + NonceSize;
    private const int CiphertextStartIndex = TagStartIndex + TagSize;

    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(Purpose);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Creates a capture token payload with the standard short expiry.</summary>
    public CodeReviewEvaluationCaptureTokenPayload CreatePayload(
        string organizationId,
        string repositoryId,
        string datasetId,
        long expectedDatasetVersion,
        CodeReviewEvaluationStagedCapture capture
    )
    {
        ArgumentNullException.ThrowIfNull(capture);

        return new(
            Version,
            Purpose,
            organizationId,
            repositoryId,
            datasetId,
            expectedDatasetVersion,
            capture.PullRequestTitle,
            capture,
            timeProvider.GetUtcNow() + CodeReviewEvaluationLimits.CaptureTokenLifetime
        );
    }

    /// <summary>Encrypts a capture token payload.</summary>
    public string Protect(CodeReviewEvaluationCaptureTokenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(GetKey(), TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

        var tokenBytes = new byte[CiphertextStartIndex + ciphertext.Length];
        tokenBytes[VersionIndex] = Version;
        nonce.CopyTo(tokenBytes.AsSpan(NonceStartIndex, NonceSize));
        tag.CopyTo(tokenBytes.AsSpan(TagStartIndex, TagSize));
        ciphertext.CopyTo(tokenBytes.AsSpan(CiphertextStartIndex));

        return Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Attempts to decrypt a capture token, optionally distinguishing expiry from tampering.
    /// </summary>
    /// <param name="token">Opaque token from the client.</param>
    /// <param name="payload">Decrypted payload when the token is authentic, even if expired.</param>
    /// <returns>
    /// <see langword="true"/> when the token is authentic and unexpired. <see langword="false"/>
    /// when it is missing, malformed, tampered or expired; callers must not reveal which.
    /// </returns>
    public bool TryUnprotect(
        string? token,
        out CodeReviewEvaluationCaptureTokenPayload? payload
    ) => TryUnprotectCore(token, out payload, validateExpiry: true);

    /// <summary>
    /// Decrypts a capture token without applying expiry, so an expired draft can return an
    /// actionable re-resolve error instead of a generic invalid-token error.
    /// </summary>
    public bool TryUnprotectIgnoringExpiry(
        string? token,
        out CodeReviewEvaluationCaptureTokenPayload? payload
    ) => TryUnprotectCore(token, out payload, validateExpiry: false);

    /// <summary>Returns the configured token lifetime.</summary>
    public static TimeSpan Lifetime => CodeReviewEvaluationLimits.CaptureTokenLifetime;

    /// <summary>
    /// Single decrypt path shared by the expiring and non-expiring readers.
    /// </summary>
    /// <remarks>
    /// Tampering, a wrong key or purpose, malformed JSON and an unsupported version all return
    /// false without revealing which check failed. Expiry is the only difference between the two
    /// callers; when it is enforced an expired token also clears the payload.
    /// </remarks>
    private bool TryUnprotectCore(
        string? token,
        out CodeReviewEvaluationCaptureTokenPayload? payload,
        bool validateExpiry
    )
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] tokenBytes;
        try
        {
            tokenBytes = DecodeBase64Url(token);
        }
        catch (FormatException)
        {
            return false;
        }

        if (tokenBytes.Length < CiphertextStartIndex || tokenBytes[VersionIndex] != Version)
        {
            return false;
        }

        var nonce = tokenBytes.AsSpan(NonceStartIndex, NonceSize);
        var tag = tokenBytes.AsSpan(TagStartIndex, TagSize);
        var ciphertext = tokenBytes.AsSpan(CiphertextStartIndex);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(GetKey(), TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            payload = JsonSerializer.Deserialize<CodeReviewEvaluationCaptureTokenPayload>(
                plaintext,
                SerializerOptions
            );
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is not { Version: Version, Purpose: Purpose, Capture: not null })
        {
            payload = null;
            return false;
        }

        if (validateExpiry && payload.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            payload = null;
            return false;
        }

        return true;
    }

    private byte[] GetKey()
    {
        if (string.IsNullOrWhiteSpace(settings.ReviewRequestLinkEncryptionKey))
        {
            throw new InvalidOperationException(
                "AppSettings:CodeReview:ReviewRequestLinkEncryptionKey is required to protect evaluation capture tokens."
            );
        }

        return SHA256.HashData(
            Encoding.UTF8.GetBytes(Purpose + "\n" + settings.ReviewRequestLinkEncryptionKey)
        );
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(base64);
    }
}

/// <summary>
/// Authenticated payload carried by one staged evaluation capture token.
/// </summary>
/// <param name="Version">Token format version.</param>
/// <param name="Purpose">Purpose string that prevents cross-use with other tokens.</param>
/// <param name="OrganizationId">Organization that owns the staged capture.</param>
/// <param name="RepositoryId">Repository mapping the capture belongs to.</param>
/// <param name="DatasetId">Dataset the save must target.</param>
/// <param name="ExpectedDatasetVersion">Dataset version the capture was staged against.</param>
/// <param name="PullRequestTitle">Captured title for the resolution preview.</param>
/// <param name="Capture">Resolved capture to install on save.</param>
/// <param name="ExpiresAtUtc">Hard UTC expiry.</param>
public sealed record CodeReviewEvaluationCaptureTokenPayload(
    byte Version,
    string Purpose,
    string OrganizationId,
    string RepositoryId,
    string DatasetId,
    long ExpectedDatasetVersion,
    string PullRequestTitle,
    CodeReviewEvaluationStagedCapture Capture,
    DateTimeOffset ExpiresAtUtc
);
