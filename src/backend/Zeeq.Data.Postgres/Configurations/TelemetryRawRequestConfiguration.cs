using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for the <c>UNLOGGED</c> raw telemetry request table.
/// </summary>
/// <remarks>
/// The backing table is deliberately unlogged — rows survive process crashes but
/// not database restarts, which is acceptable for best-effort telemetry. The table
/// is not partitioned (rows live minutes, claimed → processed → deleted).
/// Lease fields (<c>processing_lease_id</c>, <c>processing_lease_expires_at_utc</c>)
/// support the cluster-lease claim pattern: multiple concurrent processors claim
/// batches without stepping on each other, and expired leases are reclaimable on
/// crash.
/// </remarks>
internal sealed class TelemetryRawRequestConfiguration
    : IEntityTypeConfiguration<TelemetryRawRequest>
{
    public void Configure(EntityTypeBuilder<TelemetryRawRequest> entity)
    {
        entity.ToTable("telemetry_raw_requests");

        entity.HasKey(r => r.Id);

        entity.Property(r => r.Id).HasMaxLength(128).IsRequired();
        entity.Property(r => r.ReceivedAtUtc).IsRequired();
        entity
            .Property(r => r.SignalType)
            .HasConversion(new EnumToStringConverter<TelemetrySignalType>())
            .HasMaxLength(16)
            .IsRequired();
        entity.Property(r => r.IngestUserId).HasMaxLength(128);
        entity.Property(r => r.IngestOrganizationId).HasMaxLength(128);
        entity.Property(r => r.SourceName).HasMaxLength(64);
        entity.Property(r => r.HarnessName).HasMaxLength(64);
        entity.Property(r => r.RecordCount).IsRequired();
        entity.Property(r => r.Payload).IsRequired();
        entity
            .Property(r => r.ProcessingStatus)
            .HasConversion(new EnumToStringConverter<TelemetryRawRequestProcessingStatus>())
            .HasMaxLength(16)
            .IsRequired();
        entity.Property(r => r.ProcessingLeaseId).HasMaxLength(128);
        entity.Property(r => r.ProcessingLeaseExpiresAtUtc);
        entity.Property(r => r.EffectiveObfuscationPolicyVersion).HasDefaultValue(0);
        entity.Property(r => r.AttemptCount).HasDefaultValue(0);
        entity.Property(r => r.QuarantineReason).HasMaxLength(512);
        entity.Property(r => r.QuarantinedAtUtc);

        entity.HasIndex(r => r.ProcessingStatus);
        entity.HasIndex(r => r.IngestOrganizationId);
    }
}
