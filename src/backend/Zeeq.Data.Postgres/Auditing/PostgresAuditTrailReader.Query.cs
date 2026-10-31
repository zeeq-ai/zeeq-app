using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;

namespace Zeeq.Data.Postgres.Auditing;

// Shared filtering and bounded paging. Callers apply tenant/team scope before these helpers.
internal sealed partial class PostgresAuditTrailReader
{
    private static AuditHistoryCursor? Validate(AuditHistoryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit is < 1 or > AuditLimits.MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                $"Limit must be between 1 and {AuditLimits.MaxPageSize}."
            );
        }

        if (query is { FromUtc: { } from, ToUtc: { } to } && from > to)
        {
            throw new ArgumentException("FromUtc must not be after ToUtc.", nameof(query));
        }

        if (
            query.ActorId is { Length: > AuditLimits.MaxIdentifierLength }
            || query.SubjectId is { Length: > AuditLimits.MaxIdentifierLength }
            || query.SubjectType is { Length: > 64 }
            || query.LibraryId is { Length: > 128 }
        )
        {
            throw new ArgumentException("A filter value is too long.", nameof(query));
        }

        if (
            query.ChangeType is { } changeType && !Enum.IsDefined(changeType)
            || query.Area is { } area && !Enum.IsDefined(area)
        )
        {
            throw new ArgumentException("Undefined change type or area filter.", nameof(query));
        }

        return string.IsNullOrEmpty(query.Cursor) ? null : AuditHistoryCursor.Parse(query.Cursor);
    }

    private static IQueryable<AuditRow> Filter(
        IQueryable<AuditRow> rows,
        AuditHistoryQuery query,
        AuditHistoryCursor? cursor
    )
    {
        if (query.FromUtc is { } from)
        {
            rows = rows.Where(r => r.OccurredAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            rows = rows.Where(r => r.OccurredAtUtc <= to);
        }

        if (query.ChangeType is { } changeType)
        {
            var value = changeType.ToString();
            rows = rows.Where(r => r.ChangeType == value);
        }

        if (query.Area is { } area)
        {
            var value = area.ToString();
            rows = rows.Where(r => r.Area == value);
        }

        if (!string.IsNullOrEmpty(query.ActorId))
        {
            rows = rows.Where(r => r.ActorId == query.ActorId);
        }

        if (!string.IsNullOrEmpty(query.SubjectType))
        {
            rows = rows.Where(r => r.SubjectType == query.SubjectType);
        }

        if (!string.IsNullOrEmpty(query.SubjectId))
        {
            rows = rows.Where(r => r.SubjectId == query.SubjectId);
        }

        if (!string.IsNullOrEmpty(query.LibraryId))
        {
            rows = rows.Where(r => r.LibraryId == query.LibraryId);
        }

        if (query.OperationId is { } operationId)
        {
            rows = rows.Where(r => r.OperationId == operationId);
        }

        if (query.RestrictToLibraryIds is { } libraryIds)
        {
            var ids = libraryIds.ToArray();
            rows = rows.Where(r => r.LibraryId != null && ids.Contains(r.LibraryId));
        }

        if (cursor is { } position)
        {
            var at = position.OccurredAtUtc;
            var id = position.Id;
            // NOTE: Npgsql translates CompareTo into PostgreSQL uuid comparisons. Keep
            // this predicate server-side; the system merge uses the same byte order.
            rows = rows.Where(r =>
                r.OccurredAtUtc < at || (r.OccurredAtUtc == at && r.Id.CompareTo(id) < 0)
            );
        }

        return rows;
    }

    private static Task<List<AuditRow>> PageAsync(
        IQueryable<AuditRow> rows,
        int limit,
        CancellationToken ct
    ) =>
        rows.OrderByDescending(r => r.OccurredAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

    private static AuditHistoryPage ToPage(List<AuditRow> rows, int limit)
    {
        var hasMore = rows.Count > limit;
        var entries = rows.Take(limit).Select(r => r.ToSummary()).ToList();
        var nextCursor = hasMore
            ? new AuditHistoryCursor(entries[^1].OccurredAtUtc, entries[^1].Id).Encode()
            : null;

        return new AuditHistoryPage(entries, nextCursor, AuditCoverage.FirstSlice);
    }

    /// <summary>Compares Guids in the byte order Postgres uses for <c>uuid</c>.</summary>
    /// <remarks>SQL order, the keyset predicate, and this in-memory merge must agree at timestamp ties or pages can skip/repeat events.</remarks>
    private sealed class GuidByteOrderComparer : IComparer<Guid>
    {
        public static readonly GuidByteOrderComparer Instance = new();

        public int Compare(Guid x, Guid y) =>
            x.ToByteArray(bigEndian: true)
                .AsSpan()
                .SequenceCompareTo(y.ToByteArray(bigEndian: true));
    }
}
