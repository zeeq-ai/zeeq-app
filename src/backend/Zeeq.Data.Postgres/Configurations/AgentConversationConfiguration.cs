using System.Text.Json;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for the <c>agent_conversations</c> table (non-partitioned).
/// </summary>
internal sealed class AgentConversationConfiguration : IEntityTypeConfiguration<AgentConversation>
{
    public void Configure(EntityTypeBuilder<AgentConversation> entity)
    {
        entity.ToTable("agent_conversations");
        entity.HasKey(c => new { c.OrganizationId, c.Id });

        entity.Property(c => c.Id).HasMaxLength(128).IsRequired();
        entity.Property(c => c.OrganizationId).HasMaxLength(128).IsRequired();
        entity.Property(c => c.Harness).HasMaxLength(64).IsRequired();
        entity.Property(c => c.HarnessVariant).HasMaxLength(64);
        entity.Property(c => c.AppVersion).HasMaxLength(64);
        entity.Property(c => c.RepoRemoteUrl).HasMaxLength(512);
        entity.Property(c => c.HeadBranch).HasMaxLength(256);
        entity.Property(c => c.HeadSha).HasMaxLength(64);
        entity.Property(c => c.StartedAtUtc).IsRequired();
        entity.Property(c => c.TotalInputTokens).HasDefaultValue(0);
        entity.Property(c => c.TotalOutputTokens).HasDefaultValue(0);
        entity.Property(c => c.TotalCostUsd).HasColumnType("numeric(14,6)");
        entity.Property(c => c.OwnerEmail).HasMaxLength(320);
        entity.Property(c => c.CreatedById).HasMaxLength(128);
        entity
            .Property(c => c.OwnershipStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        entity.Property(c => c.SoftDeleteMetadata).HasColumnType("jsonb");

        entity.HasIndex(c => new
        {
            c.OrganizationId,
            c.Harness,
            c.StartedAtUtc,
        });
        entity.HasIndex(c => new { c.OrganizationId, c.OwnerEmail });
        entity
            .HasIndex(c => new
            {
                c.OrganizationId,
                c.HeadBranch,
                c.RepoRemoteUrl,
            })
            .HasDatabaseName("ix_agent_conversations_organization_id_head_branch_repo_remote");
    }
}
