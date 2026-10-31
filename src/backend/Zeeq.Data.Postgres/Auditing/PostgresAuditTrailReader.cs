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
/// Metadata projections live in <see cref="AuditEventQueryExtensions"/>; shared filter/paging code
/// lives in the Query partial. Organization/team scoping stays in the entry methods here; neither
/// a projection nor a shared filter can infer the caller's scope.
/// </remarks>
internal sealed partial class PostgresAuditTrailReader(PostgresDbContext db) : IAuditTrailReader
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

        var projected = rows.SelectAuditMetadata();

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

        var row = await rows.SelectAuditDetails().SingleOrDefaultAsync(ct);

        return row is null ? null : row.ToDetail();
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
            .SelectAuditMetadata();

        var surfacedRows = db
            .OrganizationAuditEvents.TagWithOperationCallSite("audit.system.list_surfaced")
            .AsNoTracking()
            .Where(e => e.SurfaceInSystem)
            .SelectAuditMetadata();

        // Each side returns at most limit + 1 rows in the shared (occurred desc, id desc) order,
        // so the merged first limit + 1 rows are exact. Keep filtering and Take on both database
        // branches; loading full histories before this merge would defeat bounded feed reads.
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
            .SelectAuditDetails()
            .SingleOrDefaultAsync(ct);

        if (system is not null)
        {
            return system.ToDetail();
        }

        var surfaced = await db
            .OrganizationAuditEvents.TagWithOperationCallSite("audit.system.detail_surfaced")
            .AsNoTracking()
            .Where(e => e.SurfaceInSystem && e.OccurredAtUtc == occurredAtUtc && e.Id == eventId)
            .SelectAuditDetails()
            .SingleOrDefaultAsync(ct);

        return surfaced is null ? null : surfaced.ToDetail();
    }
}
