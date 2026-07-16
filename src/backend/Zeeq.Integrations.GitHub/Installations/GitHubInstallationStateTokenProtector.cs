using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Common;
using Microsoft.IdentityModel.Tokens;

namespace Zeeq.Integrations.GitHub;

/// <summary>
/// Protects short-lived GitHub App installation state tokens.
/// </summary>
/// <remarks>
/// Use case: the authenticated Zeeq admin starts installation at
/// <c>/api/v1/integrations/github/install/link</c>, then GitHub redirects the
/// browser back to the anonymous setup callback. The callback may arrive on a
/// devtunnel or public host that does not carry the local Zeeq auth cookie, so
/// this token carries the minimal authenticated context needed to link the
/// installation: organization id, optional team id, user id, nonce, and expiry.
///
/// The token is URL-safe and opaque. It is not a durable credential and should
/// only be accepted during its short expiry window.
/// </remarks>
public sealed class GitHubInstallationStateTokenProtector(GitHubSettings settings)
{
    private const string Purpose = "Zeeq.GitHub.InstallationState.v1";
    private const byte Version = 1;
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
    /// Creates an opaque URL-safe state token.
    /// </summary>
    /// <remarks>
    /// The returned string is sent as GitHub's <c>state</c> query parameter.
    /// AES-GCM gives both confidentiality and tamper detection; the purpose
    /// string is supplied as associated data so a token protected for another
    /// purpose cannot be accepted here.
    /// </remarks>
    public string Protect(GitHubInstallationStatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
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

        return Base64UrlEncoder.Encode(tokenBytes);
    }

    /// <summary>
    /// Attempts to read and validate an installation state token.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for missing, malformed, unsupported
    /// version, tampered, non-JSON, or expired tokens. Callers should treat all
    /// failure modes the same and reject the callback without revealing which
    /// validation step failed.
    /// </remarks>
    public bool TryUnprotect(string? token, out GitHubInstallationStatePayload? payload)
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] tokenBytes;
        try
        {
            tokenBytes = Base64UrlEncoder.DecodeBytes(token);
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
            payload = JsonSerializer.Deserialize<GitHubInstallationStatePayload>(plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (
            payload is not { ExpiresAtUtc: var expiresAtUtc }
            || expiresAtUtc <= DateTimeOffset.UtcNow
        )
        {
            payload = null;
            return false;
        }

        return true;
    }

    private byte[] GetKey()
    {
        if (string.IsNullOrWhiteSpace(settings.PrivateKeyPem))
        {
            throw new InvalidOperationException(
                "AppSettings:GitHub:PrivateKeyPem is required to protect GitHub installation state."
            );
        }

        // Derive a compact purpose-specific key from the GitHub App private key.
        // This avoids adding a broader data-protection dependency for the phase
        // one install flow while keeping tokens invalid across app/key changes.
        return SHA256.HashData(Encoding.UTF8.GetBytes(Purpose + "\n" + settings.PrivateKeyPem));
    }
}

/// <summary>
/// Authenticated state carried through the GitHub App installation redirect.
/// </summary>
/// <param name="OrganizationId">Zeeq organization selected when the admin started installation.</param>
/// <param name="TeamId">Optional Zeeq team context selected when installation started.</param>
/// <param name="UserId">Authenticated Zeeq user that initiated the install flow.</param>
/// <param name="Nonce">Random value reserved for replay diagnostics and future one-time state tracking.</param>
/// <param name="ExpiresAtUtc">Hard UTC expiry; expired tokens are rejected even if decryption succeeds.</param>
public sealed record GitHubInstallationStatePayload(
    string OrganizationId,
    string? TeamId,
    string UserId,
    string Nonce,
    DateTimeOffset ExpiresAtUtc
);
