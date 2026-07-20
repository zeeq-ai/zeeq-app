namespace Zeeq.Core.Common;

/// <summary>
/// Helpers for timestamps that must round-trip through PostgreSQL and still compare exactly.
/// </summary>
public static class PostgresTimestampPrecision
{
    /// <summary>
    /// Truncates a timestamp to PostgreSQL's microsecond precision.
    /// </summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero
        );
    }
}
