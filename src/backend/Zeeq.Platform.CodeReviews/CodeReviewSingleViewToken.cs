using System.Buffers.Binary;
using System.Buffers.Text;

namespace Zeeq.Platform.CodeReviews;

/// <summary>
/// Compact, URL-safe token that carries the two values the single-review deep link
/// needs: the partition timestamp (<c>createdAtUtc</c>) and the view <c>mode</c>.
/// </summary>
/// <remarks>
/// The deep link is placed in GitHub comments and the browser address bar, so a
/// bare ISO timestamp (<c>2026-07-04T19:06:12.5831850+00:00</c>) plus a mode query
/// value renders long and percent-escaped. Packing both into nine bytes
/// (<see cref="DateTimeOffset.UtcTicks"/> big-endian + one mode byte) and encoding
/// with Base64Url yields a ~12 character token with no characters that require URL
/// escaping. <c>UtcTicks</c> preserves the exact value used by the equality-based
/// partition lookup, so decoding reproduces the original timestamp bit-for-bit.
/// </remarks>
public static class CodeReviewSingleViewToken
{
    private const int PayloadLength = sizeof(long) + 1;

    /// <summary>
    /// Encodes the partition timestamp and view mode into a URL-safe token.
    /// </summary>
    public static string Encode(DateTimeOffset createdAtUtc, CodeReviewSingleViewMode mode)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        BinaryPrimitives.WriteInt64BigEndian(payload, createdAtUtc.UtcTicks);
        payload[sizeof(long)] = (byte)mode;

        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Decodes a token produced by <see cref="Encode"/>. Returns <c>false</c> for null,
    /// malformed, or out-of-range input so callers can surface a bad-request error.
    /// </summary>
    public static bool TryDecode(
        string? token,
        out DateTimeOffset createdAtUtc,
        out CodeReviewSingleViewMode mode
    )
    {
        createdAtUtc = default;
        mode = default;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // TryDecodeFromChars throws on illegal characters (it only returns false for a
        // too-small destination), so validate the alphabet first.
        if (Base64Url.IsValid(token) is false)
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[PayloadLength];
        if (
            Base64Url.TryDecodeFromChars(token, payload, out var written) is false
            || written != PayloadLength
        )
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        var modeByte = payload[sizeof(long)];
        if (Enum.IsDefined((CodeReviewSingleViewMode)modeByte) is false)
        {
            return false;
        }

        createdAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        mode = (CodeReviewSingleViewMode)modeByte;
        return true;
    }
}
