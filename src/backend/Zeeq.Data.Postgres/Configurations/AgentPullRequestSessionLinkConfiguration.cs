using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF mapping for the <c>agent_pull_request_session_links</c> table (non-partitioned).
/// </summary>
internal sealed class AgentPullRequestSessionLinkConfiguration
    : IEntityTypeConfiguration<AgentPullRequestSessionLink>
{
    public void Configure(EntityTypeBuilder<AgentPullRequestSessionLink> entity)
    {
        entity.ToTable("agent_pull_request_session_links");
        entity.HasKey(l => l.Id);

        entity.Property(l => l.Id).HasMaxLength(128).IsRequired();
        entity.Property(l => l.OrganizationId).HasMaxLength(128).IsRequired();
        entity.Property(l => l.PullRequestRecordId).HasMaxLength(128).IsRequired();
        entity.Property(l => l.ConversationId).HasMaxLength(128).IsRequired();
        entity.Property(l => l.LinkOrigin).HasConversion<string>().HasMaxLength(32).IsRequired();
        entity.Property(l => l.LinkedAtUtc).IsRequired();
        entity.Property(l => l.LinkedByUserId).HasMaxLength(128);
        entity.Property(l => l.IsPending).IsRequired().HasDefaultValue(false);

        entity
            .HasIndex(l => new
            {
                l.OrganizationId,
                l.PullRequestRecordId,
                l.ConversationId,
            })
            .IsUnique();
        entity.HasIndex(l => new { l.OrganizationId, l.ConversationId });
    }
}
