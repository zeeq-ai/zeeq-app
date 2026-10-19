using System.Globalization;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Metrics.Tests;

/// <summary>Unit coverage for the deterministic HTTP range resolver.</summary>
public sealed class MetricRangeQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 13, 45, 0, TimeSpan.Zero);

    [Test]
    [Arguments("2026-10-19", "2026-10-19", 1, 1)]
    [Arguments("2026-10-01", "2026-10-02", 2, 2)]
    [Arguments("2026-10-01", "2026-10-03", 3, 2)]
    [Arguments("2026-09-27", "2026-09-30", 4, 6)]
    [Arguments("2026-09-24", "2026-09-30", 7, 6)]
    [Arguments("2026-09-23", "2026-09-30", 8, 12)]
    [Arguments("2026-09-17", "2026-09-30", 14, 12)]
    [Arguments("2026-09-16", "2026-09-30", 15, 24)]
    [Arguments("2026-09-01", "2026-09-30", 30, 24)]
    public async Task TryResolve_AbsoluteRange_UsesInclusiveDaysAndDocumentedBucket(
        string startDate,
        string endDate,
        int selectedDays,
        int bucketHours
    )
    {
        var resolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate: startDate,
            endDate: endDate,
            now: Now,
            out var range,
            out var cacheIdentity,
            out var error
        );

        await Assert.That(resolved).IsTrue();
        await Assert.That(error).IsNull();
        await Assert
            .That(range.StartUtc)
            .IsEqualTo(
                new DateTimeOffset(
                    DateOnly.ParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    TimeOnly.MinValue,
                    TimeSpan.Zero
                )
            );
        await Assert.That(range.Bucket).IsEqualTo(TimeSpan.FromHours(bucketHours));
        var phase =
            DateOnly.ParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            == DateOnly.FromDateTime(Now.UtcDateTime)
                ? "current"
                : "completed";
        await Assert
            .That(cacheIdentity)
            .IsEqualTo(
                $"absolute:{startDate}:{endDate}:{TimeSpan.FromHours(bucketHours).Ticks}:{phase}"
            );

        var requestedEndExclusive = new DateTimeOffset(
            DateOnly.ParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1),
            TimeOnly.MinValue,
            TimeSpan.Zero
        );
        var expectedEnd = requestedEndExclusive < Now ? requestedEndExclusive : Now;
        await Assert.That(range.EndExclusiveUtc).IsEqualTo(expectedEnd);
        await Assert.That(range.StartUtc.AddDays(selectedDays)).IsEqualTo(requestedEndExclusive);
    }

    [Test]
    public async Task TryResolve_AbsoluteRange_UsesUtcAndCapsPartialTodayAtNow()
    {
        var offsetNow = new DateTimeOffset(2026, 10, 19, 9, 45, 0, TimeSpan.FromHours(-4));
        var resolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate: "2026-10-19",
            endDate: "2026-10-19",
            now: offsetNow,
            out var range,
            out var cacheIdentity,
            out var error
        );

        await Assert.That(resolved).IsTrue();
        await Assert.That(error).IsNull();
        await Assert
            .That(range.StartUtc)
            .IsEqualTo(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(range.EndExclusiveUtc).IsEqualTo(offsetNow.ToUniversalTime());
        await Assert.That(range.Bucket).IsEqualTo(TimeSpan.FromHours(1));
        await Assert
            .That(cacheIdentity)
            .IsEqualTo($"absolute:2026-10-19:2026-10-19:{TimeSpan.FromHours(1).Ticks}:current");
    }

    [Test]
    public async Task TryResolve_TodayAtUtcMidnight_IsValidEmptyInterval()
    {
        var midnight = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        var resolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate: "2026-10-19",
            endDate: "2026-10-19",
            now: midnight,
            out var range,
            out var cacheIdentity,
            out var error
        );

        await Assert.That(resolved).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(range.StartUtc).IsEqualTo(midnight);
        await Assert.That(range.EndExclusiveUtc).IsEqualTo(midnight);
        await Assert
            .That(cacheIdentity)
            .IsEqualTo($"absolute:2026-10-19:2026-10-19:{TimeSpan.FromHours(1).Ticks}:current");
    }

    [Test]
    public async Task TryResolve_AbsoluteRange_ChangesIdentityWhenSelectedEndDateBecomesCompletedAtUtcMidnight()
    {
        var justBeforeMidnight = new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.Zero);
        var justAfterMidnight = new DateTimeOffset(2026, 10, 20, 0, 0, 1, TimeSpan.Zero);
        var beforeResolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate: "2026-10-18",
            endDate: "2026-10-19",
            now: justBeforeMidnight,
            out _,
            out var beforeIdentity,
            out var beforeError
        );
        var afterResolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate: "2026-10-18",
            endDate: "2026-10-19",
            now: justAfterMidnight,
            out _,
            out var afterIdentity,
            out var afterError
        );

        await Assert.That(beforeResolved).IsTrue();
        await Assert.That(afterResolved).IsTrue();
        await Assert.That(beforeError).IsNull();
        await Assert.That(afterError).IsNull();
        await Assert
            .That(beforeIdentity)
            .IsEqualTo($"absolute:2026-10-18:2026-10-19:{TimeSpan.FromHours(2).Ticks}:current");
        await Assert
            .That(afterIdentity)
            .IsEqualTo($"absolute:2026-10-18:2026-10-19:{TimeSpan.FromHours(2).Ticks}:completed");
        await Assert.That(afterIdentity).IsNotEqualTo(beforeIdentity);
    }

    [Test]
    public async Task TryResolve_AbsoluteRange_IdentityIsStableWithinCurrentAndCompletedPhases()
    {
        var currentIdentities = new[]
        {
            ResolveIdentity(
                "2026-10-18",
                "2026-10-19",
                new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero)
            ),
            ResolveIdentity(
                "2026-10-18",
                "2026-10-19",
                new DateTimeOffset(2026, 10, 19, 23, 59, 59, TimeSpan.Zero)
            ),
        };
        var completedIdentities = new[]
        {
            ResolveIdentity(
                "2026-10-18",
                "2026-10-19",
                new DateTimeOffset(2026, 10, 20, 0, 0, 1, TimeSpan.Zero)
            ),
            ResolveIdentity(
                "2026-10-18",
                "2026-10-19",
                new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)
            ),
        };

        await Assert.That(currentIdentities[0]).IsEqualTo(currentIdentities[1]);
        await Assert.That(currentIdentities[0]).EndsWith(":current");
        await Assert.That(completedIdentities[0]).IsEqualTo(completedIdentities[1]);
        await Assert.That(completedIdentities[0]).EndsWith(":completed");
    }

    [Test]
    public async Task TryResolve_OldLeapAndYearBoundaryRange_IsAccepted()
    {
        var resolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate: "2020-12-31",
            endDate: "2021-01-01",
            Now,
            out var range,
            out var cacheIdentity,
            out var error
        );

        await Assert.That(resolved).IsTrue();
        await Assert.That(error).IsNull();
        await Assert
            .That(range.StartUtc)
            .IsEqualTo(new DateTimeOffset(2020, 12, 31, 0, 0, 0, TimeSpan.Zero));
        await Assert
            .That(range.EndExclusiveUtc)
            .IsEqualTo(new DateTimeOffset(2021, 1, 2, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(range.Bucket).IsEqualTo(TimeSpan.FromHours(2));
        await Assert
            .That(cacheIdentity)
            .IsEqualTo($"absolute:2020-12-31:2021-01-01:{TimeSpan.FromHours(2).Ticks}:completed");
    }

    [Test]
    public async Task TryResolve_OldLeapDayRange_IsAccepted()
    {
        var resolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate: "2024-02-28",
            endDate: "2024-02-29",
            now: Now,
            out var range,
            out _,
            out var error
        );

        await Assert.That(resolved).IsTrue();
        await Assert.That(error).IsNull();
        await Assert
            .That(range.StartUtc)
            .IsEqualTo(new DateTimeOffset(2024, 2, 28, 0, 0, 0, TimeSpan.Zero));
        await Assert
            .That(range.EndExclusiveUtc)
            .IsEqualTo(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));
        await Assert.That(range.Bucket).IsEqualTo(TimeSpan.FromHours(2));
    }

    [Test]
    public async Task TryResolve_EquivalentRelativeTokens_HaveStableClockIndependentIdentity()
    {
        var firstNow = Now;
        var secondNow = Now.AddMinutes(1);
        var firstResolved = MetricRangeQuery.TryResolve(
            "24h",
            null,
            null,
            firstNow,
            out var firstRange,
            out var firstIdentity,
            out var firstError
        );
        var secondResolved = MetricRangeQuery.TryResolve(
            "24H",
            null,
            null,
            secondNow,
            out var secondRange,
            out var secondIdentity,
            out var secondError
        );

        await Assert.That(firstResolved).IsTrue();
        await Assert.That(secondResolved).IsTrue();
        await Assert.That(firstError).IsNull();
        await Assert.That(secondError).IsNull();
        await Assert.That(firstIdentity).IsEqualTo("relative:H24");
        await Assert.That(secondIdentity).IsEqualTo(firstIdentity);
        await Assert.That(firstRange.StartUtc).IsNotEqualTo(secondRange.StartUtc);
        await Assert.That(firstRange.EndExclusiveUtc).IsEqualTo(firstNow);
        await Assert.That(secondRange.EndExclusiveUtc).IsEqualTo(secondNow);
    }

    [Test]
    [Arguments(null, null, null, "invalid_window")]
    [Arguments("not-a-window", null, null, "invalid_window")]
    [Arguments("", null, null, "invalid_window")]
    [Arguments("24h", "2026-10-18", "2026-10-19", "invalid_range")]
    [Arguments("", "2026-10-18", "2026-10-19", "invalid_range")]
    [Arguments(null, null, "2026-10-19", "invalid_range")]
    [Arguments(null, "2026-10-18", null, "invalid_range")]
    [Arguments(null, "", "", "invalid_range")]
    [Arguments(null, "2026-1-01", "2026-10-02", "invalid_range")]
    [Arguments(null, "10/01/2026", "2026-10-02", "invalid_range")]
    [Arguments(null, "2026-10-01T00:00:00Z", "2026-10-02", "invalid_range")]
    [Arguments(null, "2026-10-01,2026-10-02", "2026-10-02", "invalid_range")]
    [Arguments(null, "2026-10-01", "2026-10-02,2026-10-03", "invalid_range")]
    [Arguments(null, "2026-02-30", "2026-03-01", "invalid_range")]
    [Arguments(null, "2026-10-01 ", "2026-10-02", "invalid_range")]
    [Arguments(null, "2026-10-03", "2026-10-02", "invalid_range")]
    [Arguments(null, "2026-09-01", "2026-10-01", "invalid_range")]
    [Arguments(null, "2026-10-20", "2026-10-20", "invalid_range")]
    public async Task TryResolve_InvalidInput_ReturnsExpectedError(
        string? window,
        string? startDate,
        string? endDate,
        string expectedCode
    )
    {
        var resolved = MetricRangeQuery.TryResolve(
            window,
            startDate,
            endDate,
            Now,
            out _,
            out _,
            out var error
        );

        await Assert.That(resolved).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo(expectedCode);
    }

    private static string ResolveIdentity(string startDate, string endDate, DateTimeOffset now)
    {
        var resolved = MetricRangeQuery.TryResolve(
            window: null,
            startDate,
            endDate,
            now,
            out _,
            out var cacheIdentity,
            out var error
        );

        if (!resolved || error is not null)
        {
            throw new InvalidOperationException($"Test range failed to resolve: {error?.Code}");
        }

        return cacheIdentity;
    }
}
