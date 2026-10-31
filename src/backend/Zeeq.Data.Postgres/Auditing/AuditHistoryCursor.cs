using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>Opaque keyset position (<c>occurred_at_utc</c>, <c>id</c>) ordered newest first.</summary>
internal readonly record struct AuditHistoryCursor(DateTimeOffset OccurredAtUtc, Guid Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes(
                $"{OccurredAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{Id:N}"
            )
        );

    /// <summary>Parses a cursor; throws <see cref="ArgumentException"/> for malformed input.</summary>
    public static AuditHistoryCursor Parse(string cursor)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = text.Split('|');

            if (
                parts.Length == 2
                && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
                && ticks >= 0
                && ticks <= DateTime.MaxValue.Ticks
                && Guid.TryParseExact(parts[1], "N", out var id)
            )
            {
                return new AuditHistoryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            }
        }
        catch (FormatException) { }
        catch (ArgumentException) { }

        throw new ArgumentException("The cursor is not valid.", nameof(cursor));
    }
}
