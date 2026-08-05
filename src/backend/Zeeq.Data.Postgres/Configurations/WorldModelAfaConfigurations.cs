using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Zeeq.Data.Postgres.WorldModel;
using Zeeq.Platform.WorldModel.Afa;

namespace Zeeq.Data.Postgres.Configurations;

internal sealed class WorldModelNodeConfiguration : IEntityTypeConfiguration<WorldModelNodeRow>
{
    public void Configure(EntityTypeBuilder<WorldModelNodeRow> entity)
    {
        entity.ToTable(
            "awm_nodes",
            table =>
            {
                table.HasCheckConstraint(
                    "ck_awm_nodes_kind",
                    "kind IN ('area', 'feature', 'action')"
                );
                table.HasCheckConstraint("ck_awm_nodes_version", "version >= 1");
                table.HasCheckConstraint(
                    "ck_awm_nodes_semantic_revision",
                    "semantic_revision >= 1"
                );
            }
        );
        entity.HasKey(row => new { row.OrganizationId, row.Id });
        entity.Property(row => row.OrganizationId).HasMaxLength(128).IsRequired();
        entity.Property(row => row.TeamId).HasMaxLength(128);
        entity
            .Property(row => row.Kind)
            .HasMaxLength(8)
            .HasConversion(
                kind => kind.ToString().ToLowerInvariant(),
                value => Enum.Parse<WorldModelNodeKind>(value, true)
            )
            .IsRequired();
        entity.Property(row => row.Segment).HasMaxLength(128).IsRequired();
        entity.Property(row => row.Path).HasMaxLength(512).IsRequired();
        entity.Property(row => row.Obsolete).HasColumnType("jsonb");
        entity.Property(row => row.IsEffectivelyObsolete).HasDefaultValue(false).IsRequired();
        entity.Property(row => row.Version).HasDefaultValue(1L).IsRequired();
        entity.Property(row => row.SemanticRevision).HasDefaultValue(1L).IsRequired();
        entity.HasIndex(row => new { row.OrganizationId, row.Path }).IsUnique();
        entity
            .HasIndex(row => new
            {
                row.OrganizationId,
                row.ParentId,
                row.Segment,
            })
            .IsUnique();
        entity
            .HasOne<WorldModelNodeRow>()
            .WithMany()
            .HasForeignKey(row => new { row.OrganizationId, row.ParentId })
            .HasPrincipalKey(row => new { row.OrganizationId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorldModelBodyItemConfiguration
    : IEntityTypeConfiguration<WorldModelBodyItemRow>
{
    public void Configure(EntityTypeBuilder<WorldModelBodyItemRow> entity)
    {
        entity.ToTable(
            "awm_body_items",
            table =>
            {
                table.HasCheckConstraint("ck_awm_body_items_kind", "kind IN ('rule', 'flow')");
                table.HasCheckConstraint("ck_awm_body_items_revision", "revision >= 1");
            }
        );
        entity.HasKey(row => new { row.OrganizationId, row.Id });
        entity.Property(row => row.OrganizationId).HasMaxLength(128).IsRequired();
        entity
            .Property(row => row.Kind)
            .HasMaxLength(8)
            .HasConversion(
                kind => kind.ToString().ToLowerInvariant(),
                value => Enum.Parse<WorldModelBodyKind>(value, true)
            )
            .IsRequired();
        entity.Property(row => row.Name).HasMaxLength(256).IsRequired();
        entity.Property(row => row.Content).IsRequired();
        entity
            .Property(row => row.Participants)
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'")
            .IsRequired();
        entity.Property(row => row.Obsolete).HasColumnType("jsonb");
        entity.Property(row => row.Revision).HasDefaultValueSql("nextval('zeeq.awm_revision_seq')");
        entity.Property(row => row.RepoPrSha).HasMaxLength(128);
        entity.HasIndex(row => new
        {
            row.OrganizationId,
            row.NodeId,
            row.Kind,
        });
        entity
            .HasIndex(row => new { row.OrganizationId, row.Participants })
            .HasDatabaseName("ix_awm_body_items_participants")
            .HasMethod("gin");
        entity
            .HasOne<WorldModelNodeRow>()
            .WithMany()
            .HasForeignKey(row => new { row.OrganizationId, row.NodeId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
