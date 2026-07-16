using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Common;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Creates and validates encrypted tokens that authorize MCP diff uploads.
/// </summary>
/// <remarks>
/// Uploaded diffs are written through an anonymous HTTP endpoint, so the token
/// carries the job identity, owner, organization, expiry, and optional W3C
/// trace context needed to bind the upload back to the authenticated MCP tool
/// call that created the URL. AES-GCM provides confidentiality and tamper
/// detection, and the distinct purpose string prevents cross-use with review
/// request link tokens derived from the same configured key material.
/// </remarks>
public sealed class CodeReviewDiffUploadTokenProtector(CodeReviewSettings settings)
{
    private const byte Version = 1;
    private const string Purpose = "Zeeq.CodeReview.DiffUpload.v1";
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

    /// <summary>
    /// Creates the canonical payload for a new uploaded-diff job.
    /// </summary>
    public static CodeReviewDiffUploadTokenPayload CreatePayload(
        string jobId,
        DateTimeOffset expiresAtUtc,
        string createdById,
        string organizationId,
        ZeeqTraceContext? traceContext = null
    ) =>
        new(
            Version: Version,
            Purpose: Purpose,
            JobId: jobId,
            ExpiresAtUtc: expiresAtUtc,
            CreatedById: createdById,
            OrganizationId: organizationId,
            TraceParent: traceContext?.TraceParent,
            TraceState: traceContext?.TraceState
        );

    /// <summary>
    /// Encrypts an upload token payload.
    /// </summary>
    public string Protect(CodeReviewDiffUploadTokenPayload payload)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key.Value, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);

        var tokenBytes = new byte[CiphertextStartIndex + ciphertext.Length];
        tokenBytes[VersionIndex] = Version;
        nonce.CopyTo(tokenBytes.AsSpan(NonceStartIndex, NonceSize));
        tag.CopyTo(tokenBytes.AsSpan(TagStartIndex, TagSize));
        ciphertext.CopyTo(tokenBytes.AsSpan(CiphertextStartIndex));

        return EncodeBase64Url(tokenBytes);
    }

    /// <summary>
    /// Attempts to decrypt and validate an MCP diff-upload token.
    /// </summary>
    public bool TryUnprotect(
        string? token,
        out CodeReviewDiffUploadTokenPayload? payload,
        bool validateExpiry = true
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
            using var aes = new AesGcm(_key.Value, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            payload = JsonSerializer.Deserialize<CodeReviewDiffUploadTokenPayload>(plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (!IsValid(payload, validateExpiry))
        {
            payload = null;
            return false;
        }

        return true;
    }

    internal TimeSpan GetValidity()
    {
        if (settings.DiffUploadUrlValidityMinutes <= 0)
        {
            throw new InvalidOperationException(
                "AppSettings:CodeReview:DiffUploadUrlValidityMinutes must be greater than zero."
            );
        }

        return TimeSpan.FromMinutes(settings.DiffUploadUrlValidityMinutes);
    }

    private static bool IsValid(CodeReviewDiffUploadTokenPayload? payload, bool validateExpiry) =>
        payload
            is {
                Version: Version,
                Purpose: Purpose,
                JobId.Length: > 0,
                CreatedById.Length: > 0,
                OrganizationId.Length: > 0,
                ExpiresAtUtc: var expiresAtUtc,
            }
        && (!validateExpiry || expiresAtUtc > DateTimeOffset.UtcNow);

    // Key is derived once per singleton lifetime: SHA256(Purpose + "\n" + configured key).
    // Lazy<T> defers validation to first use and caches the result, eliminating repeated
    // hashing and byte-array allocation on every Protect/TryUnprotect call.
    private readonly Lazy<byte[]> _key = new(() =>
    {
        if (string.IsNullOrWhiteSpace(settings.ReviewRequestLinkEncryptionKey))
        {
            throw new InvalidOperationException(
                "AppSettings:CodeReview:ReviewRequestLinkEncryptionKey is required to protect MCP code review diff upload tokens."
            );
        }

        return SHA256.HashData(
            Encoding.UTF8.GetBytes(Purpose + "\n" + settings.ReviewRequestLinkEncryptionKey)
        );
    });

    private static string EncodeBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
/// Decrypted payload carried by an MCP diff-upload token.
/// </summary>
/// <param name="Version">Payload schema version.</param>
/// <param name="Purpose">Purpose string that prevents cross-use with other token types.</param>
/// <param name="JobId">Uploaded-diff job id.</param>
/// <param name="ExpiresAtUtc">Hard UTC expiry for the upload and review run.</param>
/// <param name="CreatedById">Authenticated user that created the upload URL.</param>
/// <param name="OrganizationId">Zeeq organization that owns the uploaded diff.</param>
/// <param name="TraceParent">Optional W3C traceparent captured when the URL was created.</param>
/// <param name="TraceState">Optional W3C tracestate captured when the URL was created.</param>
public sealed record CodeReviewDiffUploadTokenPayload(
    byte Version,
    string Purpose,
    string JobId,
    DateTimeOffset ExpiresAtUtc,
    string CreatedById,
    string OrganizationId,
    string? TraceParent = null,
    string? TraceState = null
);
