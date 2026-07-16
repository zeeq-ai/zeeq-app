using System.Text.Json;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for the <c>agent_session_events</c> table (range-partitioned).
/// </summary>
/// <remarks>
/// The actual partitioning DDL is applied by the hand-edited migration — EF cannot
/// express <c>PARTITION BY RANGE</c>. This configuration defines the column shape
/// and indexes that the migration SQL will create on the parent partitioned table.
/// </remarks>
internal sealed class AgentSessionEventConfiguration : IEntityTypeConfiguration<AgentSessionEvent>
{
    public void Configure(EntityTypeBuilder<AgentSessionEvent> entity)
    {
        entity.ToTable("agent_session_events");
        entity.HasKey(e => new { e.Id, e.OccurredAtUtc });

        entity.Property(e => e.Id).HasMaxLength(128).IsRequired();
        entity.Property(e => e.OrganizationId).HasMaxLength(128).IsRequired();
        entity.Property(e => e.ConversationId).HasMaxLength(128).IsRequired();
        entity.Property(e => e.OccurredAtUtc).IsRequired();
        entity.Property(e => e.SourceRecordId).HasMaxLength(128);
        entity.Property(e => e.PromptGroupId).HasMaxLength(128);
        entity.Property(e => e.ToolCallId).HasMaxLength(128);
        entity.Property(e => e.ProviderRequestId).HasMaxLength(128);
        entity.Property(e => e.ToolName).HasMaxLength(512);
        entity.Property(e => e.ToolNameRaw).HasMaxLength(512);
        entity.Property(e => e.McpServer).HasMaxLength(256);
        entity.Property(e => e.McpServerOrigin).HasMaxLength(256);
        entity.Property(e => e.McpServerScope).HasMaxLength(256);
        entity
            .Property(e => e.ArgumentsJson)
            .HasColumnType("jsonb");
        entity.Property(e => e.Decision).HasMaxLength(64);
        entity.Property(e => e.DecisionSource).HasMaxLength(128);
        entity.Property(e => e.Model).HasMaxLength(128);
        entity.Property(e => e.CostUsd).HasColumnType("numeric(14,6)");
        entity.Property(e => e.QuerySource).HasMaxLength(256);
        entity.Property(e => e.IsHousekeeping).IsRequired().HasDefaultValue(false);

        entity.HasIndex(e => new
        {
            e.OrganizationId,
            e.ConversationId,
            e.OccurredAtUtc,
        });
        entity
            .HasIndex(e => new
            {
                e.OrganizationId,
                e.EventType,
                e.OccurredAtUtc,
            })
            .HasFilter("event_type IN (2, 3)");
    }
}
