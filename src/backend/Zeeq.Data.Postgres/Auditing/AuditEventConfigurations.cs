using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Auditing;

/// <summary>
/// EF mapping for the range-partitioned <c>organization_audit_events</c> table.
/// </summary>
/// <remarks>
/// NOTE: The table is partitioned monthly (UTC) on <c>occurred_at_utc</c> by pg_partman; the
/// hand-edited migration creates the partitioned parent, indexes, and partman config. Retention
/// is intentionally NULL: partitions are never detached or dropped automatically. Time predicates
/// prune partitions; organization-leading keys/indexes bound tenant scans within them. The partial
/// system-surface index is the exception: authorized cross-tenant reads must not scan every tenant.
/// Partitioning bounds the recent working set, not the total retained index/storage size.
/// </remarks>
internal sealed class OrganizationAuditEventConfiguration
    : IEntityTypeConfiguration<OrganizationAuditEvent>
{
    public void Configure(EntityTypeBuilder<OrganizationAuditEvent> entity)
    {
        entity.ToTable("organization_audit_events");
        // PostgreSQL requires partition keys in a partitioned-table primary key. Organization
        // leads the tenant identity; timestamp is also required for an exact, prunable detail read.
        entity.HasKey(e => new
        {
            e.OrganizationId,
            e.OccurredAtUtc,
            e.Id,
        });

        entity.Property(e => e.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(e => e.TeamId).HasMaxLength(128);
        entity.Property(e => e.OrganizationLabel).HasMaxLength(512);
        AuditEventColumns.Configure(entity);

        entity.HasIndex(e => new
        {
            e.OrganizationId,
            e.LibraryId,
            e.OccurredAtUtc,
            e.Id,
        });
        entity.HasIndex(e => new
        {
            e.OrganizationId,
            e.SubjectType,
            e.SubjectId,
            e.OccurredAtUtc,
            e.Id,
        });
        entity.HasIndex(e => new
        {
            e.OrganizationId,
            e.OperationId,
            e.OccurredAtUtc,
        });
        entity
            .HasIndex(e => new { e.OccurredAtUtc, e.Id })
            .HasFilter("surface_in_system")
            .HasDatabaseName("ix_organization_audit_events_system_surface");
    }
}

/// <summary>EF mapping for the range-partitioned <c>system_audit_events</c> table.</summary>
/// <remarks>See <see cref="OrganizationAuditEventConfiguration"/>; same partitioning and retention.</remarks>
internal sealed class SystemAuditEventConfiguration : IEntityTypeConfiguration<SystemAuditEvent>
{
    public void Configure(EntityTypeBuilder<SystemAuditEvent> entity)
    {
        entity.ToTable("system_audit_events");
        entity.HasKey(e => new { e.OccurredAtUtc, e.Id });

        entity.Property(e => e.OrganizationLabel).HasMaxLength(512);
        AuditEventColumns.Configure(entity);

        entity.HasIndex(e => new
        {
            e.SubjectType,
            e.SubjectId,
            e.OccurredAtUtc,
            e.Id,
        });
        entity.HasIndex(e => new { e.OperationId, e.OccurredAtUtc });
    }
}

/// <summary>Column mapping shared by the two non-inheriting event models (same envelope).</summary>
internal static class AuditEventColumns
{
    public static void Configure<T>(EntityTypeBuilder<T> entity)
        where T : class
    {
        entity.Property<Guid>("Id").ValueGeneratedNever();
        entity.Property<int>("SchemaVersion").HasDefaultValue(1);
        entity.Property<string>("EventType").IsRequired().HasMaxLength(128);
        entity.Property<string>("ChangeType").IsRequired().HasMaxLength(32);
        entity.Property<string>("Area").IsRequired().HasMaxLength(64);
        entity.Property<string>("SubjectType").IsRequired().HasMaxLength(64);
        entity.Property<string>("SubjectId").IsRequired().HasMaxLength(256);
        entity.Property<string>("SubjectLabel").IsRequired().HasMaxLength(512);
        entity.Property<string?>("LibraryId").HasMaxLength(128);
        entity.Property<string>("ActorKind").IsRequired().HasMaxLength(64);
        entity.Property<string>("ActorId").IsRequired().HasMaxLength(256);
        entity.Property<string>("ActorDisplayName").IsRequired().HasMaxLength(512);
        entity.Property<string>("ActorChannel").IsRequired().HasMaxLength(64);
        entity.Property<string?>("ActorResponsibleUserId").HasMaxLength(256);
        entity.Property<string?>("ActorCredentialId").HasMaxLength(256);
        entity.Property<string?>("ActorCredentialName").HasMaxLength(512);
        entity.Property<string?>("ActorInitiatedByUserId").HasMaxLength(256);
        entity.Property<string?>("RequestId").HasMaxLength(256);
        entity.Property<string[]>("ChangedFields").IsRequired().HasColumnType("text[]");
        entity.Property<string?>("SourceReference").HasMaxLength(512);
        entity.Property<string?>("BeforeHash").HasMaxLength(128);
        entity.Property<string?>("AfterHash").HasMaxLength(128);
        entity.Property<string?>("Outcome").HasMaxLength(64);
        // Headers and snapshots share retention, so a separate payload table buys no expiry
        // boundary. Native TOAST handles large jsonb values; feeds must still project metadata
        // explicitly to avoid fetching them. No payload GIN index is needed for timeline reads.
        entity.Property<string?>("BeforeSnapshot").HasColumnType("jsonb");
        entity.Property<string?>("AfterSnapshot").HasColumnType("jsonb");
    }
}
