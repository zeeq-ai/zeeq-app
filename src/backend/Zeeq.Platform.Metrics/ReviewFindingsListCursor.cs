using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Compact, URL-safe keyset-pagination cursor for the findings drill-down list endpoint — encodes
/// the last row's <c>(review_created_at_utc, review_id)</c> so the next page's query can resume
/// exactly where the previous one left off.
/// </summary>
/// <remarks>
/// Same encode/decode shape as <c>IngestRunListCursor</c> (itself copying
/// <c>CodeReviewSingleViewToken</c>'s pattern), duplicated here rather than shared across modules
/// per the project's convention of small per-module codecs over a cross-project dependency. Ordering
/// is <c>review_created_at_utc DESC, review_id DESC</c> — see
/// <c>PostgresMetricsQueryStore.ListFindingReviewGroupsAsync</c>.
/// </remarks>
public static class ReviewFindingsListCursor
{
    /// <summary>Encodes the last row of a page into a cursor for the next page.</summary>
    public static string Encode(DateTimeOffset reviewCreatedAtUtc, string reviewId)
    {
        var idBytes = Encoding.UTF8.GetBytes(reviewId);
        Span<byte> payload = stackalloc byte[sizeof(long) + idBytes.Length];
        BinaryPrimitives.WriteInt64BigEndian(payload, reviewCreatedAtUtc.UtcTicks);
        idBytes.CopyTo(payload[sizeof(long)..]);

        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/>. Returns <c>false</c> for null, empty
    /// (first-page), malformed, or out-of-range input.
    /// </summary>
    public static bool TryDecode(
        string? cursor,
        out DateTimeOffset reviewCreatedAtUtc,
        out string reviewId
    )
    {
        reviewCreatedAtUtc = default;
        reviewId = "";

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

        reviewCreatedAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
        // NOTE: No version/length marker on the id payload — any non-empty trailing bytes decode
        // as a reviewId. Deferred: this is an internal, server-minted, short-lived cursor (not a
        // persisted or user-authored format), so there's no compatibility surface to protect yet.
        // Add a version prefix if the payload shape ever needs to evolve independently of callers.
        reviewId = Encoding.UTF8.GetString(payload[sizeof(long)..written]);
        return reviewId.Length > 0;
    }
}
