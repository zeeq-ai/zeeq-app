using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zeeq.Data.Postgres.WorldModel;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for organization-scoped world-model scheduler state.
/// </summary>
internal sealed class WorldModelSchedulerQueueStateConfiguration
    : IEntityTypeConfiguration<WorldModelSchedulerQueueStateRow>
{
    public void Configure(EntityTypeBuilder<WorldModelSchedulerQueueStateRow> entity)
    {
        entity.ToTable("awm_scheduler_queue_state");
        entity.HasKey(row => new
        {
            row.OrganizationId,
            row.Tier,
            row.Bucket,
        });
        entity.Property(row => row.OrganizationId).IsRequired().HasMaxLength(128);
        entity
            .Property(row => row.Tier)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(
                tier => WorldModelSchedulerStorageValues.Format(tier),
                value => WorldModelSchedulerStorageValues.ParseTier(value)
            );
        entity.Property(row => row.Bucket).IsRequired();
        entity.Property(row => row.Deficit).IsRequired();
        entity.Property(row => row.ActiveTargetCount).IsRequired();
        // Active lanes are visited oldest-first; the partial index excludes permanently idle rows.
        entity
            .HasIndex(row => new
            {
                row.Tier,
                row.Bucket,
                row.LastVisitedAtUtc,
                row.OrganizationId,
            })
            .HasDatabaseName("ix_awm_scheduler_queue_state_active")
            .HasFilter("active_target_count > 0");
    }
}

/// <summary>
/// EF mapping for consumer-scoped world-model scheduler targets and leases.
/// </summary>
internal sealed class WorldModelSchedulerPendingTargetConfiguration
    : IEntityTypeConfiguration<WorldModelSchedulerPendingTargetRow>
{
    public void Configure(EntityTypeBuilder<WorldModelSchedulerPendingTargetRow> entity)
    {
        entity.ToTable("awm_scheduler_pending_targets");
        entity.HasKey(row => new
        {
            row.OrganizationId,
            row.Consumer,
            row.TargetId,
        });
        entity.Property(row => row.OrganizationId).IsRequired().HasMaxLength(128);
        entity
            .Property(row => row.Consumer)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(
                consumer => WorldModelSchedulerStorageValues.Format(consumer),
                value => WorldModelSchedulerStorageValues.ParseConsumer(value)
            );
        entity.Property(row => row.TargetId).IsRequired().HasMaxLength(128);
        entity
            .Property(row => row.Tier)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(
                tier => WorldModelSchedulerStorageValues.Format(tier),
                value => WorldModelSchedulerStorageValues.ParseTier(value)
            );
        entity.Property(row => row.Bucket).IsRequired();
        entity.Property(row => row.EventCount).IsRequired();
        entity.Property(row => row.EstimatedCost).IsRequired();
        entity.Property(row => row.OldestEventAtUtc).IsRequired();
        entity.Property(row => row.NewestEventAtUtc).IsRequired();
        entity.Property(row => row.AggregateRevision).IsRequired();
        entity.Property(row => row.LeasedBy).HasMaxLength(128);
        // Claims only inspect unleased targets. Consumer and target id break timestamp ties while
        // the partial predicate keeps leased work out of the FIFO scan.
        entity
            .HasIndex(row => new
            {
                row.OrganizationId,
                row.Tier,
                row.Bucket,
                row.OldestEventAtUtc,
                row.Consumer,
                row.TargetId,
            })
            .HasDatabaseName("ix_awm_scheduler_pending_targets_available")
            .HasFilter("leased_by IS NULL");
        // The global reclaimer starts with expiry and only needs rows that currently have an owner.
        entity
            .HasIndex(row => row.LeaseExpiresAtUtc)
            .HasDatabaseName("ix_awm_scheduler_pending_targets_expired_lease")
            .HasFilter("leased_by IS NOT NULL");
        // A target cannot outlive or drift away from the lane whose deficit pays for it.
        entity
            .HasOne<WorldModelSchedulerQueueStateRow>()
            .WithMany()
            .HasForeignKey(row => new
            {
                row.OrganizationId,
                row.Tier,
                row.Bucket,
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
