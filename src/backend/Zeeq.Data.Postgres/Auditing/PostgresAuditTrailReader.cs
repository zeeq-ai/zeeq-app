using Microsoft.EntityFrameworkCore;
using Zeeq.Core.Auditing;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>
/// Reads audit history. List queries project metadata only and never select snapshot columns.
/// </summary>
/// <remarks>
/// Authorization (who may call, which team/library restrictions apply) is decided at the
/// endpoint boundary and passed in as <c>teamId</c> (team-scoped plus org-shared events) / <see cref="AuditHistoryQuery.RestrictToLibraryIds"/>;
/// this reader enforces those scopes in every query. The system view merges the global table
/// with tenant events flagged <c>surface_in_system</c> (sysadmin-actor changes), so one canonical
/// event is visible in both views without duplication.
/// </remarks>
internal sealed class PostgresAuditTrailReader(PostgresDbContext db) : IAuditTrailReader
{
    /// <inheritdoc />
    public async Task<AuditHistoryPage> ListOrganizationAsync(
        string organizationId,
        string? teamId,
        AuditHistoryQuery query,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var cursor = Validate(query);

        var rows = db
            .OrganizationAuditEvents.TagWithOperationCallSite("audit.organization.list")
            .AsNoTracking()
            .Where(e => e.OrganizationId == organizationId);

        if (teamId is not null)
        {
            rows = rows.Where(e => e.TeamId == null || e.TeamId == teamId);
        }

        var projected = rows.Select(e => new AuditRow
        {
            Id = e.Id,
            OccurredAtUtc = e.OccurredAtUtc,
            Scope = AuditScope.Organization,
            OrganizationId = e.OrganizationId,
            OrganizationLabel = e.OrganizationLabel,
            TeamId = e.TeamId,
            EventType = e.EventType,
            ChangeType = e.ChangeType,
            Area = e.Area,
            SubjectType = e.SubjectType,
            SubjectId = e.SubjectId,
            SubjectLabel = e.SubjectLabel,
            LibraryId = e.LibraryId,
            ActorKind = e.ActorKind,
            ActorId = e.ActorId,
            ActorDisplayName = e.ActorDisplayName,
            ActorChannel = e.ActorChannel,
            ActorResponsibleUserId = e.ActorResponsibleUserId,
            ActorCredentialId = e.ActorCredentialId,
            ActorCredentialName = e.ActorCredentialName,
            ActorInitiatedByUserId = e.ActorInitiatedByUserId,
            OperationId = e.OperationId,
            ChangedFields = e.ChangedFields,
            SourceReference = e.SourceReference,
            BeforeHash = e.BeforeHash,
            AfterHash = e.AfterHash,
            Outcome = e.Outcome,
            HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
        });

        var page = await PageAsync(Filter(projected, query, cursor), query.Limit, ct);

        return ToPage(page, query.Limit);
    }

    /// <inheritdoc />
    public async Task<AuditEventDetail?> GetOrganizationEventAsync(
        string organizationId,
        string? teamId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);

        var rows = db
            .OrganizationAuditEvents.TagWithOperationCallSite("audit.organization.detail")
            .AsNoTracking()
            .Where(e =>
                e.OrganizationId == organizationId
                && e.OccurredAtUtc == occurredAtUtc
                && e.Id == eventId
            );

        if (teamId is not null)
        {
            rows = rows.Where(e => e.TeamId == null || e.TeamId == teamId);
        }

        var row = await rows.Select(e => new
            {
                Summary = new AuditRow
                {
                    Id = e.Id,
                    OccurredAtUtc = e.OccurredAtUtc,
                    Scope = AuditScope.Organization,
                    OrganizationId = e.OrganizationId,
                    OrganizationLabel = e.OrganizationLabel,
                    TeamId = e.TeamId,
                    EventType = e.EventType,
                    ChangeType = e.ChangeType,
                    Area = e.Area,
                    SubjectType = e.SubjectType,
                    SubjectId = e.SubjectId,
                    SubjectLabel = e.SubjectLabel,
                    LibraryId = e.LibraryId,
                    ActorKind = e.ActorKind,
                    ActorId = e.ActorId,
                    ActorDisplayName = e.ActorDisplayName,
                    ActorChannel = e.ActorChannel,
                    ActorResponsibleUserId = e.ActorResponsibleUserId,
                    ActorCredentialId = e.ActorCredentialId,
                    ActorCredentialName = e.ActorCredentialName,
                    ActorInitiatedByUserId = e.ActorInitiatedByUserId,
                    OperationId = e.OperationId,
                    ChangedFields = e.ChangedFields,
                    SourceReference = e.SourceReference,
                    BeforeHash = e.BeforeHash,
                    AfterHash = e.AfterHash,
                    Outcome = e.Outcome,
                    HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
                },
                e.BeforeSnapshot,
                e.AfterSnapshot,
            })
            .SingleOrDefaultAsync(ct);

        return row is null
            ? null
            : new AuditEventDetail(
                row.Summary.ToSummary(),
                AuditSnapshotSerializer.Deserialize(row.BeforeSnapshot),
                AuditSnapshotSerializer.Deserialize(row.AfterSnapshot)
            );
    }

    /// <inheritdoc />
    public async Task<AuditHistoryPage> ListSystemAsync(
        AuditHistoryQuery query,
        CancellationToken ct
    )
    {
        var cursor = Validate(query);

        var systemRows = db
            .SystemAuditEvents.TagWithOperationCallSite("audit.system.list")
            .AsNoTracking()
            .Select(e => new AuditRow
            {
                Id = e.Id,
                OccurredAtUtc = e.OccurredAtUtc,
                Scope = AuditScope.System,
                OrganizationLabel = e.OrganizationLabel,
                EventType = e.EventType,
                ChangeType = e.ChangeType,
                Area = e.Area,
                SubjectType = e.SubjectType,
                SubjectId = e.SubjectId,
                SubjectLabel = e.SubjectLabel,
                LibraryId = e.LibraryId,
                ActorKind = e.ActorKind,
                ActorId = e.ActorId,
                ActorDisplayName = e.ActorDisplayName,
                ActorChannel = e.ActorChannel,
                ActorResponsibleUserId = e.ActorResponsibleUserId,
                ActorCredentialId = e.ActorCredentialId,
                ActorCredentialName = e.ActorCredentialName,
                ActorInitiatedByUserId = e.ActorInitiatedByUserId,
                OperationId = e.OperationId,
                ChangedFields = e.ChangedFields,
                SourceReference = e.SourceReference,
                BeforeHash = e.BeforeHash,
                AfterHash = e.AfterHash,
                Outcome = e.Outcome,
                HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
            });

        var surfacedRows = db
            .OrganizationAuditEvents.TagWithOperationCallSite("audit.system.list_surfaced")
            .AsNoTracking()
            .Where(e => e.SurfaceInSystem)
            .Select(e => new AuditRow
            {
                Id = e.Id,
                OccurredAtUtc = e.OccurredAtUtc,
                Scope = AuditScope.Organization,
                OrganizationId = e.OrganizationId,
                OrganizationLabel = e.OrganizationLabel,
                TeamId = e.TeamId,
                EventType = e.EventType,
                ChangeType = e.ChangeType,
                Area = e.Area,
                SubjectType = e.SubjectType,
                SubjectId = e.SubjectId,
                SubjectLabel = e.SubjectLabel,
                LibraryId = e.LibraryId,
                ActorKind = e.ActorKind,
                ActorId = e.ActorId,
                ActorDisplayName = e.ActorDisplayName,
                ActorChannel = e.ActorChannel,
                ActorResponsibleUserId = e.ActorResponsibleUserId,
                ActorCredentialId = e.ActorCredentialId,
                ActorCredentialName = e.ActorCredentialName,
                ActorInitiatedByUserId = e.ActorInitiatedByUserId,
                OperationId = e.OperationId,
                ChangedFields = e.ChangedFields,
                SourceReference = e.SourceReference,
                BeforeHash = e.BeforeHash,
                AfterHash = e.AfterHash,
                Outcome = e.Outcome,
                HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
            });

        // Each side returns at most limit + 1 rows in the shared (occurred desc, id desc) order,
        // so the merged first limit + 1 rows are exact.
        var fromSystem = await PageAsync(Filter(systemRows, query, cursor), query.Limit, ct);
        var fromOrganizations = await PageAsync(
            Filter(surfacedRows, query, cursor),
            query.Limit,
            ct
        );

        var merged = fromSystem
            .Concat(fromOrganizations)
            .OrderByDescending(r => r.OccurredAtUtc)
            .ThenByDescending(r => r.Id, GuidByteOrderComparer.Instance)
            .Take(query.Limit + 1)
            .ToList();

        return ToPage(merged, query.Limit);
    }

    /// <inheritdoc />
    public async Task<AuditEventDetail?> GetSystemEventAsync(
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken ct
    )
    {
        var system = await db
            .SystemAuditEvents.TagWithOperationCallSite("audit.system.detail")
            .AsNoTracking()
            .Where(e => e.OccurredAtUtc == occurredAtUtc && e.Id == eventId)
            .Select(e => new
            {
                Summary = new AuditRow
                {
                    Id = e.Id,
                    OccurredAtUtc = e.OccurredAtUtc,
                    Scope = AuditScope.System,
                    OrganizationLabel = e.OrganizationLabel,
                    EventType = e.EventType,
                    ChangeType = e.ChangeType,
                    Area = e.Area,
                    SubjectType = e.SubjectType,
                    SubjectId = e.SubjectId,
                    SubjectLabel = e.SubjectLabel,
                    LibraryId = e.LibraryId,
                    ActorKind = e.ActorKind,
                    ActorId = e.ActorId,
                    ActorDisplayName = e.ActorDisplayName,
                    ActorChannel = e.ActorChannel,
                    ActorResponsibleUserId = e.ActorResponsibleUserId,
                    ActorCredentialId = e.ActorCredentialId,
                    ActorCredentialName = e.ActorCredentialName,
                    ActorInitiatedByUserId = e.ActorInitiatedByUserId,
                    OperationId = e.OperationId,
                    ChangedFields = e.ChangedFields,
                    SourceReference = e.SourceReference,
                    BeforeHash = e.BeforeHash,
                    AfterHash = e.AfterHash,
                    Outcome = e.Outcome,
                    HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
                },
                e.BeforeSnapshot,
                e.AfterSnapshot,
            })
            .SingleOrDefaultAsync(ct);

        if (system is not null)
        {
            return new AuditEventDetail(
                system.Summary.ToSummary(),
                AuditSnapshotSerializer.Deserialize(system.BeforeSnapshot),
                AuditSnapshotSerializer.Deserialize(system.AfterSnapshot)
            );
        }

        var surfaced = await db
            .OrganizationAuditEvents.TagWithOperationCallSite("audit.system.detail_surfaced")
            .AsNoTracking()
            .Where(e => e.SurfaceInSystem && e.OccurredAtUtc == occurredAtUtc && e.Id == eventId)
            .Select(e => new
            {
                Summary = new AuditRow
                {
                    Id = e.Id,
                    OccurredAtUtc = e.OccurredAtUtc,
                    Scope = AuditScope.Organization,
                    OrganizationId = e.OrganizationId,
                    OrganizationLabel = e.OrganizationLabel,
                    TeamId = e.TeamId,
                    EventType = e.EventType,
                    ChangeType = e.ChangeType,
                    Area = e.Area,
                    SubjectType = e.SubjectType,
                    SubjectId = e.SubjectId,
                    SubjectLabel = e.SubjectLabel,
                    LibraryId = e.LibraryId,
                    ActorKind = e.ActorKind,
                    ActorId = e.ActorId,
                    ActorDisplayName = e.ActorDisplayName,
                    ActorChannel = e.ActorChannel,
                    ActorResponsibleUserId = e.ActorResponsibleUserId,
                    ActorCredentialId = e.ActorCredentialId,
                    ActorCredentialName = e.ActorCredentialName,
                    ActorInitiatedByUserId = e.ActorInitiatedByUserId,
                    OperationId = e.OperationId,
                    ChangedFields = e.ChangedFields,
                    SourceReference = e.SourceReference,
                    BeforeHash = e.BeforeHash,
                    AfterHash = e.AfterHash,
                    Outcome = e.Outcome,
                    HasSnapshots = e.BeforeSnapshot != null || e.AfterSnapshot != null,
                },
                e.BeforeSnapshot,
                e.AfterSnapshot,
            })
            .SingleOrDefaultAsync(ct);

        return surfaced is null
            ? null
            : new AuditEventDetail(
                surfaced.Summary.ToSummary(),
                AuditSnapshotSerializer.Deserialize(surfaced.BeforeSnapshot),
                AuditSnapshotSerializer.Deserialize(surfaced.AfterSnapshot)
            );
    }

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
    private sealed class GuidByteOrderComparer : IComparer<Guid>
    {
        public static readonly GuidByteOrderComparer Instance = new();

        public int Compare(Guid x, Guid y) =>
            x.ToByteArray(bigEndian: true)
                .AsSpan()
                .SequenceCompareTo(y.ToByteArray(bigEndian: true));
    }

    /// <summary>Metadata-only projection shared by both event tables. Has no snapshot members.</summary>
    private sealed class AuditRow
    {
        public Guid Id { get; set; }
        public DateTimeOffset OccurredAtUtc { get; set; }
        public AuditScope Scope { get; set; }
        public string? OrganizationId { get; set; }
        public string? OrganizationLabel { get; set; }
        public string? TeamId { get; set; }
        public string EventType { get; set; } = null!;
        public string ChangeType { get; set; } = null!;
        public string Area { get; set; } = null!;
        public string SubjectType { get; set; } = null!;
        public string SubjectId { get; set; } = null!;
        public string SubjectLabel { get; set; } = null!;
        public string? LibraryId { get; set; }
        public string ActorKind { get; set; } = null!;
        public string ActorId { get; set; } = null!;
        public string ActorDisplayName { get; set; } = null!;
        public string ActorChannel { get; set; } = null!;
        public string? ActorResponsibleUserId { get; set; }
        public string? ActorCredentialId { get; set; }
        public string? ActorCredentialName { get; set; }
        public string? ActorInitiatedByUserId { get; set; }
        public Guid OperationId { get; set; }
        public string[] ChangedFields { get; set; } = [];
        public string? SourceReference { get; set; }
        public string? BeforeHash { get; set; }
        public string? AfterHash { get; set; }
        public string? Outcome { get; set; }
        public bool HasSnapshots { get; set; }

        public AuditEventSummary ToSummary() =>
            new()
            {
                Id = Id,
                OccurredAtUtc = OccurredAtUtc,
                Scope = Scope,
                OrganizationId = OrganizationId,
                OrganizationLabel = OrganizationLabel,
                TeamId = TeamId,
                EventType = EventType,
                ChangeType = Enum.TryParse<AuditChangeType>(ChangeType, out var changeType)
                    ? changeType
                    : AuditChangeType.Edited,
                Area = Enum.TryParse<AuditArea>(Area, out var area) ? area : AuditArea.Settings,
                SubjectType = SubjectType,
                SubjectId = SubjectId,
                SubjectLabel = SubjectLabel,
                LibraryId = LibraryId,
                Actor = new AuditActor(
                    ActorKind,
                    ActorId,
                    ActorDisplayName,
                    ActorChannel,
                    ActorResponsibleUserId,
                    ActorCredentialId,
                    ActorCredentialName,
                    ActorInitiatedByUserId
                ),
                OperationId = OperationId,
                ChangedFields = ChangedFields,
                SourceReference = SourceReference,
                BeforeHash = BeforeHash,
                AfterHash = AfterHash,
                Outcome = Outcome,
                HasSnapshots = HasSnapshots,
            };
    }
}
