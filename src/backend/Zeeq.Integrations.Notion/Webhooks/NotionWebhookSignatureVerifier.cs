using System.Security.Cryptography;
using System.Text;

namespace Zeeq.Integrations.Notion;

/// <summary>Validates Notion webhook signatures against the exact request bytes.</summary>
public sealed class NotionWebhookSignatureVerifier
{
    private const string Scheme = "sha256=";
    private const int Sha256HexLength = 64;

    /// <summary>
    /// Returns whether <paramref name="signatureHeader"/> is the HMAC-SHA256 of
    /// <paramref name="rawBody"/> keyed by <paramref name="verificationToken"/>.
    /// </summary>
    public bool IsValid(
        ReadOnlySpan<byte> rawBody,
        string verificationToken,
        string? signatureHeader
    )
    {
        if (
            string.IsNullOrEmpty(verificationToken)
            || signatureHeader is null
            || !signatureHeader.StartsWith(Scheme, StringComparison.Ordinal)
            || signatureHeader.Length != Scheme.Length + Sha256HexLength
        )
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signatureHeader.AsSpan(Scheme.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(verificationToken), rawBody, expected);

        return supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
