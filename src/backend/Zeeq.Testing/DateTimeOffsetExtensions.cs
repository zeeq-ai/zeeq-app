namespace Zeeq.Testing;

/// <summary>
/// Test helpers for timestamps persisted through PostgreSQL.
/// </summary>
public static class DateTimeOffsetExtensions
{
    /// <summary>
    /// Truncates to PostgreSQL <c>timestamp with time zone</c> microsecond precision so exact equality assertions survive a database round-trip.
    /// </summary>
    public static DateTimeOffset TruncateToPostgresPrecision(this DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);
}
