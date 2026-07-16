using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Creates and validates encrypted review request link tokens.
/// </summary>
/// <remarks>
/// GitHub comments are rendered outside Zeeq, so action links must be
/// stateless and safe to carry in a public PR comment. This protector encrypts
/// the minimum review identity and budget context needed by a future endpoint
/// to accept an initial review or re-review request. AES-GCM provides both
/// confidentiality and tamper detection, and the purpose string is authenticated
/// as associated data so tokens for another purpose cannot be accepted here.
/// </remarks>
public sealed class CodeReviewRequestTokenProtector(CodeReviewSettings settings)
{
    private const byte Version = 1;
    private const string Purpose = "Zeeq.CodeReview.RequestLink.v1";
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
    /// Creates a token for the first manual review request on a pull request.
    /// </summary>
    /// <remarks>
    /// Draft PR comments use this token so a human can request review without
    /// Zeeq storing server-side session state for every rendered prompt.
    /// </remarks>
    public string ProtectInitialReview(
        string organizationId,
        string? teamId,
        string repositoryId,
        string ownerQualifiedRepoName,
        int pullRequestNumber,
        int remainingReviewBudget,
        DateTimeOffset expiresAtUtc
    ) =>
        Protect(
            new CodeReviewRequestTokenPayload(
                Version: Version,
                Purpose: Purpose,
                Kind: CodeReviewRequestTokenKind.InitialReview,
                OrganizationId: organizationId,
                TeamId: teamId,
                RepositoryId: repositoryId,
                OwnerQualifiedRepoName: ownerQualifiedRepoName,
                PullRequestNumber: pullRequestNumber,
                CodeReviewRecordId: null,
                CodeReviewCreatedAtUtc: null,
                RemainingReviewBudget: remainingReviewBudget,
                ExpiresAtUtc: expiresAtUtc
            )
        );

    /// <summary>
    /// Creates a token for requesting another review from an existing review record.
    /// </summary>
    /// <remarks>
    /// Completed-review comments bind the source review id and its partition
    /// timestamp into the token. The future endpoint can then load the exact
    /// partitioned review row before deciding whether the budget still allows a
    /// re-review.
    /// </remarks>
    public string ProtectExistingReview(
        CodeReviewRecord review,
        int remainingReviewBudget,
        DateTimeOffset expiresAtUtc
    ) =>
        Protect(
            new CodeReviewRequestTokenPayload(
                Version: Version,
                Purpose: Purpose,
                Kind: CodeReviewRequestTokenKind.ExistingReview,
                OrganizationId: review.OrganizationId,
                TeamId: review.TeamId,
                RepositoryId: review.RepositoryId,
                OwnerQualifiedRepoName: review.OwnerQualifiedRepoName,
                PullRequestNumber: review.PullRequestNumber,
                CodeReviewRecordId: review.Id,
                CodeReviewCreatedAtUtc: review.CreatedAtUtc,
                RemainingReviewBudget: remainingReviewBudget,
                ExpiresAtUtc: expiresAtUtc
            )
        );

    /// <summary>
    /// Attempts to decrypt and validate a review request token.
    /// </summary>
    /// <remarks>
    /// This method is not used by the current workflow slice, but keeping it
    /// beside the protector gives the future endpoint one canonical validation
    /// path and lets tests prove that generated links are redeemable.
    /// </remarks>
    public bool TryUnprotect(string? token, out CodeReviewRequestTokenPayload? payload)
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
            payload = JsonSerializer.Deserialize<CodeReviewRequestTokenPayload>(plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (!IsValid(payload))
        {
            payload = null;
            return false;
        }

        return true;
    }

    private string Protect(CodeReviewRequestTokenPayload payload)
    {
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

        return EncodeBase64Url(tokenBytes);
    }

    private bool IsValid(CodeReviewRequestTokenPayload? payload)
    {
        if (
            payload
                is not {
                    Version: Version,
                    Purpose: Purpose,
                    ExpiresAtUtc: var expiresAtUtc,
                    OrganizationId.Length: > 0,
                    RepositoryId.Length: > 0,
                    OwnerQualifiedRepoName.Length: > 0,
                    PullRequestNumber: > 0,
                }
            || expiresAtUtc <= DateTimeOffset.UtcNow
            || !Enum.IsDefined(payload.Kind)
        )
        {
            return false;
        }

        return payload.Kind != CodeReviewRequestTokenKind.ExistingReview
            || (
                !string.IsNullOrWhiteSpace(payload.CodeReviewRecordId)
                && payload.CodeReviewCreatedAtUtc is not null
            );
    }

    internal TimeSpan GetValidity()
    {
        if (settings.ReviewRequestLinkValidityDays <= 0)
        {
            throw new InvalidOperationException(
                "AppSettings:CodeReview:ReviewRequestLinkValidityDays must be greater than zero."
            );
        }

        return TimeSpan.FromDays(settings.ReviewRequestLinkValidityDays);
    }

    private byte[] GetKey()
    {
        if (string.IsNullOrWhiteSpace(settings.ReviewRequestLinkEncryptionKey))
        {
            throw new InvalidOperationException(
                "AppSettings:CodeReview:ReviewRequestLinkEncryptionKey is required to protect code review request links."
            );
        }

        return SHA256.HashData(
            Encoding.UTF8.GetBytes(Purpose + "\n" + settings.ReviewRequestLinkEncryptionKey)
        );
    }

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
/// Decrypted payload carried by a review request link token.
/// </summary>
/// <param name="Version">Payload schema version.</param>
/// <param name="Purpose">Purpose string that prevents cross-use with other token types.</param>
/// <param name="Kind">Whether the link requests the first review or another review.</param>
/// <param name="OrganizationId">Zeeq organization that owns the pull request.</param>
/// <param name="TeamId">Optional Zeeq team context.</param>
/// <param name="RepositoryId">Local repository mapping id.</param>
/// <param name="OwnerQualifiedRepoName">Provider repository identity, such as <c>owner/repo</c>.</param>
/// <param name="PullRequestNumber">Provider pull request number.</param>
/// <param name="CodeReviewRecordId">Source review id for re-review links.</param>
/// <param name="CodeReviewCreatedAtUtc">Source review partition timestamp for re-review links.</param>
/// <param name="RemainingReviewBudget">Remaining review budget visible when the link was rendered.</param>
/// <param name="ExpiresAtUtc">Hard UTC expiry for the link.</param>
public sealed record CodeReviewRequestTokenPayload(
    byte Version,
    string Purpose,
    CodeReviewRequestTokenKind Kind,
    string OrganizationId,
    string? TeamId,
    string? RepositoryId,
    string OwnerQualifiedRepoName,
    int PullRequestNumber,
    string? CodeReviewRecordId,
    DateTimeOffset? CodeReviewCreatedAtUtc,
    int RemainingReviewBudget,
    DateTimeOffset ExpiresAtUtc
);

/// <summary>
/// Identifies the review request workflow represented by a protected link.
/// </summary>
public enum CodeReviewRequestTokenKind
{
    /// <summary>
    /// The link requests the first review for a pull request.
    /// </summary>
    InitialReview = 0,

    /// <summary>
    /// The link requests another review using an existing review as the source.
    /// </summary>
    ExistingReview = 1,
}
