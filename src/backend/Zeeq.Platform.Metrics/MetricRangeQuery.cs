using System.Globalization;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics;

/// <summary>Validates one relative preset or an inclusive pair of UTC calendar dates.</summary>
/// <remarks>Accepts an explicit clock value so bounds and date validation are deterministic.</remarks>
internal static class MetricRangeQuery
{
    /// <summary>Resolves query input into bounded SQL parameters and a stable cache identity per range phase.</summary>
    public static bool TryResolve(
        string? window,
        string? startDate,
        string? endDate,
        DateTimeOffset now,
        out MetricQueryRange range,
        out string cacheIdentity,
        out MetricsEndpointError error
    )
    {
        range = default;
        cacheIdentity = string.Empty;
        error = null!;
        now = now.ToUniversalTime();

        // Presence, including an empty value, decides the mode before any parsing/defaulting.
        if (startDate is null && endDate is null)
        {
            if (!MetricWindowQuery.TryParse(window, out var preset))
            {
                error = new("invalid_window", $"Unknown window '{window}'.");
                return false;
            }

            var presetRange = preset.ToRange();
            range = new(now - presetRange.Span, now, presetRange.Bucket);
            // NOTE: Keep relative keys clock-independent so the 30-second TTL can coalesce polling.
            cacheIdentity = $"relative:{preset}";

            return true;
        }

        if (
            window is not null
            || !TryDate(startDate, out var start)
            || !TryDate(endDate, out var end)
        )
        {
            error = new(
                "invalid_range",
                "Supply either window or both startDate and endDate in yyyy-MM-dd format."
            );
            return false;
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var days = end.DayNumber - start.DayNumber + 1;
        if (days is < 1 or > 30 || end > today || end == DateOnly.MaxValue)
        {
            error = new(
                "invalid_range",
                "Select 1–30 inclusive UTC dates in chronological order, with no future dates."
            );
            return false;
        }

        var bucket = TimeSpan.FromHours(
            days switch
            {
                1 => 1,
                <= 3 => 2,
                <= 7 => 6,
                <= 14 => 12,
                _ => 24,
            }
        );
        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var selectedEnd = new DateTimeOffset(
            end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        );
        // NOTE: Today at exactly UTC midnight is a valid selection with an empty effective interval.
        range = new(startUtc, selectedEnd < now ? selectedEnd : now, bucket);
        // A completed date must not reuse the last cached partial-day result after UTC midnight.
        var phase = end == today ? "current" : "completed";
        cacheIdentity =
            $"absolute:{startDate}:{endDate}:{bucket.Ticks.ToString(CultureInfo.InvariantCulture)}:{phase}";

        return true;
    }

    /// <summary>Rejects timestamps, locale dates, whitespace, and joined repeated query values.</summary>
    private static bool TryDate(string? value, out DateOnly date)
    {
        date = default;
        return value is { Length: 10 }
            && DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date
            );
    }
}
