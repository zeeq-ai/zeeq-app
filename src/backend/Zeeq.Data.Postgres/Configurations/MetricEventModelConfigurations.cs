using System.Text.Json;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for the partitioned <c>zeeq_metric_events</c> wide-event table.
/// </summary>
/// <remarks>
/// NOTE: This entity maps to a range-partitioned table (by <c>created_at_utc</c>)
/// with a composite <c>(id, created_at_utc)</c> primary key, mirroring
/// <c>code_review_records</c>. pg_partman owns child partition creation and 30-day
/// retention; the hand-edited migration sets the partitioning DDL and partman
/// config, so future column/index changes must account for the partitioned parent.
///
/// There are intentionally no foreign keys: tag values are point-in-time labels,
/// not referential state, and read queries never JOIN. Every index leads with
/// <c>(organization_id, metric_type)</c> so single-organization, single-metric
/// window scans stay partition-pruned and index-covered. The sparse dimension
/// indexes are partial (<c>... IS NOT NULL</c>) so they only cover the metric
/// families that populate that column, keeping them small without hard-coding the
/// metric_type value set.
/// </remarks>
internal sealed class MetricEventConfiguration : IEntityTypeConfiguration<MetricEvent>
{
    /// <summary>
    /// Order-insensitive comparer so EF tracks jsonb tag mutations correctly and
    /// snapshots by value rather than by reference.
    /// </summary>
    private static readonly ValueComparer<Dictionary<string, string>> TagsComparer = new(
        (left, right) =>
            (left == null && right == null)
            || (
                left != null
                && right != null
                && left.Count == right.Count
                && !left.Except(right).Any()
            ),
        value =>
            value == null
                ? 0
                : value.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
        value => value == null ? new() : new Dictionary<string, string>(value)
    );

    public void Configure(EntityTypeBuilder<MetricEvent> entity)
    {
        entity.ToTable("zeeq_metric_events");
        entity.HasKey(metricEvent => new { metricEvent.Id, metricEvent.CreatedAtUtc });

        entity.Property(metricEvent => metricEvent.Id).UseIdentityAlwaysColumn();
        entity.Property(metricEvent => metricEvent.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(metricEvent => metricEvent.TeamId).HasMaxLength(128);
        entity.Property(metricEvent => metricEvent.MetricType).IsRequired().HasMaxLength(128);
        entity.Property(metricEvent => metricEvent.MetricValue).IsRequired();
        entity.Property(metricEvent => metricEvent.UserEmail).HasMaxLength(320);
        entity.Property(metricEvent => metricEvent.ToolName).HasMaxLength(128);
        entity.Property(metricEvent => metricEvent.RepositoryId).HasMaxLength(128);
        entity.Property(metricEvent => metricEvent.Library).HasMaxLength(128);
        entity.Property(metricEvent => metricEvent.Facet).HasMaxLength(64);
        entity
            .Property(metricEvent => metricEvent.Tags)
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                tags => JsonSerializer.Serialize(tags, (JsonSerializerOptions?)null),
                json =>
                    JsonSerializer.Deserialize<Dictionary<string, string>>(
                        json,
                        (JsonSerializerOptions?)null
                    ) ?? new(),
                TagsComparer
            );
        entity.Property(metricEvent => metricEvent.CreatedAtUtc).IsRequired();

        // Workhorse: every dashboard query narrows to one org + metric family + window.
        entity.HasIndex(metricEvent => new
        {
            metricEvent.OrganizationId,
            metricEvent.MetricType,
            metricEvent.CreatedAtUtc,
        });

        // Sparse dimension indexes. Partial IS NOT NULL predicates keep each index
        // scoped to the metric families that actually populate the column.
        entity
            .HasIndex(metricEvent => new
            {
                metricEvent.OrganizationId,
                metricEvent.MetricType,
                metricEvent.UserEmail,
                metricEvent.CreatedAtUtc,
            })
            .HasFilter("user_email IS NOT NULL");
        entity
            .HasIndex(metricEvent => new
            {
                metricEvent.OrganizationId,
                metricEvent.MetricType,
                metricEvent.ToolName,
                metricEvent.CreatedAtUtc,
            })
            .HasFilter("tool_name IS NOT NULL");
        entity
            .HasIndex(metricEvent => new
            {
                metricEvent.OrganizationId,
                metricEvent.MetricType,
                metricEvent.Library,
                metricEvent.CreatedAtUtc,
            })
            .HasFilter("library IS NOT NULL");
        entity
            .HasIndex(metricEvent => new
            {
                metricEvent.OrganizationId,
                metricEvent.MetricType,
                metricEvent.RepositoryId,
                metricEvent.CreatedAtUtc,
            })
            .HasFilter("repository_id IS NOT NULL");
        entity
            .HasIndex(metricEvent => new
            {
                metricEvent.OrganizationId,
                metricEvent.MetricType,
                metricEvent.Facet,
                metricEvent.CreatedAtUtc,
            })
            .HasFilter("facet IS NOT NULL");
    }
}
