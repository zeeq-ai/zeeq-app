using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Compact, URL-safe keyset-pagination cursor for the run history list
/// endpoint — encodes the last row's <c>(created_at_utc, id)</c> so the next
/// page's query can resume exactly where the previous one left off.
/// </summary>
/// <remarks>
/// Same encode/decode shape as <see cref="IngestRunViewToken"/> (itself
/// copying <c>CodeReviewSingleViewToken</c>'s pattern), extended with a
/// variable-length UTF-8 id payload since a run id isn't fixed-size. Ordering
/// is <c>created_at_utc DESC, id DESC</c> — see
/// <c>IDocsIngestRunStore.ListByLibraryAsync</c>/<c>ListByPublicSourceAsync</c>.
/// </remarks>
public static class IngestRunListCursor
{
    /// <summary>Encodes the last row of a page into a cursor for the next page.</summary>
    public static string Encode(DateTimeOffset createdAtUtc, string id)
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        Span<byte> payload = stackalloc byte[sizeof(long) + idBytes.Length];
        BinaryPrimitives.WriteInt64BigEndian(payload, createdAtUtc.UtcTicks);
        idBytes.CopyTo(payload[sizeof(long)..]);

        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/>. Returns <c>false</c>
    /// for null, empty (first-page), malformed, or out-of-range input.
    /// </summary>
    public static bool TryDecode(string? cursor, out DateTimeOffset createdAtUtc, out string id)
    {
        createdAtUtc = default;
        id = "";

        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        if (Base64Url.IsValid(cursor) is false)
        {
            return false;
        }

        var maxLength = Base64Url.GetMaxDecodedLength(cursor.Length);
        if (maxLength < sizeof(long))
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[maxLength];
        if (
            Base64Url.TryDecodeFromChars(cursor, payload, out var written) is false
            || written < sizeof(long)
        )
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(payload);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        createdAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        id = Encoding.UTF8.GetString(payload[sizeof(long)..written]);
        return id.Length > 0;
    }
}
