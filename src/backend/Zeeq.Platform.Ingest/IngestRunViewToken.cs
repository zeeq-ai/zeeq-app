using System.Buffers.Binary;
using System.Buffers.Text;
using Zeeq.Core.Documents;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Compact, URL-safe token that carries the partition key
/// (<see cref="DocsIngestRun.CreatedAtUtc"/>) and source kind for one ingest
/// run, alongside the run id in a manual-trigger response or deep link.
/// </summary>
/// <remarks>
/// Copies <c>CodeReviewSingleViewToken</c>'s pattern exactly (see that type for
/// the full rationale): <c>docs_ingest_runs</c> is partitioned by
/// <c>created_at_utc</c>, and an equality-based partition lookup needs the
/// exact timestamp value, not a re-derived one. The run id itself is not
/// encoded here — callers carry it separately (e.g. in a URL path segment),
/// exactly as <c>CodeReviewSingleViewToken</c> leaves the review id to the
/// route and only encodes what a client-supplied id can't reconstruct.
/// <see cref="RepositorySourceKind"/> rides along as the discriminator byte so
/// a viewer endpoint can apply public/private authorization rules before
/// touching the store.
/// </remarks>
public static class IngestRunViewToken
{
    private const int PayloadLength = sizeof(long) + 1;

    /// <summary>Encodes the partition timestamp and source kind into a URL-safe token.</summary>
    public static string Encode(DateTimeOffset createdAtUtc, RepositorySourceKind kind)
    {
        Span<byte> payload = stackalloc byte[PayloadLength];
        BinaryPrimitives.WriteInt64BigEndian(payload, createdAtUtc.UtcTicks);
        payload[sizeof(long)] = (byte)kind;

        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Decodes a token produced by <see cref="Encode"/>. Returns <c>false</c> for null,
    /// malformed, or out-of-range input so callers can surface a bad-request error.
    /// </summary>
    public static bool TryDecode(
        string? token,
        out DateTimeOffset createdAtUtc,
        out RepositorySourceKind kind
    )
    {
        createdAtUtc = default;
        kind = default;

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

        var kindByte = payload[sizeof(long)];
        if (Enum.IsDefined((RepositorySourceKind)kindByte) is false)
        {
            return false;
        }

        createdAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        kind = (RepositorySourceKind)kindByte;
        return true;
    }
}
