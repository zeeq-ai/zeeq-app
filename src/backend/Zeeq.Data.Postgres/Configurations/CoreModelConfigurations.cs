using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.ToTable("core_users");
        entity.HasKey(user => user.Id);
        entity.Property(user => user.Id).HasMaxLength(128);
        entity.Property(user => user.DisplayName).IsRequired().HasMaxLength(200);
        entity.Property(user => user.Email).HasMaxLength(320);
        entity.Property(user => user.PictureUrl).HasMaxLength(2048);
        entity.Property(user => user.CreatedAtUtc).IsRequired();
        entity.Property(user => user.UpdatedAtUtc).IsRequired();
        entity.HasIndex(user => user.Email);
        entity.HasIndex(user => user.DisabledAtUtc);
    }
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> entity)
    {
        entity.ToTable("core_organizations");
        entity.HasKey(organization => organization.Id);
        entity.Property(organization => organization.Id).HasMaxLength(128);
        entity.Property(organization => organization.DisplayName).IsRequired().HasMaxLength(200);
        entity.Property(organization => organization.Slug).HasMaxLength(128);
        entity.HasIndex(organization => organization.Slug).IsUnique();
        entity.Property(organization => organization.IconUrl).HasMaxLength(87380);
        entity.Property(organization => organization.ActivatedAtUtc);
        entity
            .Property(organization => organization.Tier)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>()
            .HasDefaultValue(OrganizationTier.Default);
        entity.ComplexProperty(
            organization => organization.LlmConfiguration,
            configuration =>
            {
                configuration.ToJson();
                configuration.ComplexProperty(value => value.Fast);
                configuration.ComplexProperty(value => value.High);
                configuration.ComplexProperty(value => value.Max);
            }
        );
        entity.ComplexProperty(
            organization => organization.CodeReviewConfiguration,
            configuration =>
            {
                configuration.ToJson("code_review_configuration");
                configuration.IsRequired(false);
            }
        );
        entity
            .Property(organization => organization.CreatedByUserId)
            .IsRequired()
            .HasMaxLength(128);
        entity.Property(organization => organization.CreatedAtUtc).IsRequired();
        entity.Property(organization => organization.UpdatedAtUtc).IsRequired();
        entity.HasIndex(organization => organization.CreatedByUserId);
        entity.HasIndex(organization => organization.DisabledAtUtc);
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(organization => organization.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// EF mapping for organization-owned encrypted secret values.
/// </summary>
internal sealed class EncryptedValueConfiguration : IEntityTypeConfiguration<EncryptedValue>
{
    public void Configure(EntityTypeBuilder<EncryptedValue> entity)
    {
        entity.ToTable("core_encrypted_values");
        entity.HasKey(value => new { value.OrganizationId, value.Id });

        entity.Property(value => value.Id).HasMaxLength(128);
        entity.Property(value => value.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(value => value.Kind).IsRequired().HasMaxLength(64).HasConversion<string>();
        entity.Property(value => value.EncryptionProvider).IsRequired().HasMaxLength(64);
        entity.Property(value => value.Name).HasMaxLength(200);
        entity.Property(value => value.Ciphertext).IsRequired();
        entity.Property(value => value.CreatedAtUtc).IsRequired();
        entity.Property(value => value.UpdatedAtUtc).IsRequired();

        entity.HasIndex(value => value.DisabledAtUtc);
        entity.HasIndex(value => new
        {
            value.OrganizationId,
            value.Kind,
            value.DisabledAtUtc,
        });

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(value => value.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> entity)
    {
        entity.ToTable("core_teams");
        entity.HasKey(team => team.Id);
        entity.Property(team => team.Id).HasMaxLength(128);
        entity.Property(team => team.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(team => team.DisplayName).IsRequired().HasMaxLength(200);
        entity.Property(team => team.CreatedByUserId).IsRequired().HasMaxLength(128);
        entity.Property(team => team.CreatedAtUtc).IsRequired();
        entity.Property(team => team.UpdatedAtUtc).IsRequired();
        entity.HasAlternateKey(team => new { team.OrganizationId, team.Id });
        entity.HasIndex(team => team.DisabledAtUtc);
        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(team => team.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(team => team.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrganizationMembershipConfiguration
    : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> entity)
    {
        entity.ToTable("core_organization_memberships");
        entity.HasKey(membership => membership.Id);
        entity.Property(membership => membership.Id).HasMaxLength(128);
        entity.Property(membership => membership.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(membership => membership.UserId).HasMaxLength(128);
        entity.Property(membership => membership.Role).IsRequired().HasMaxLength(64);
        entity
            .Property(membership => membership.Status)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();
        entity.Property(membership => membership.InvitedEmail).HasMaxLength(320);
        entity.Property(membership => membership.CreatedByUserId).IsRequired().HasMaxLength(128);
        entity.Property(membership => membership.CreatedAtUtc).IsRequired();
        entity.HasIndex(membership => membership.DisabledAtUtc);

        // Lookups
        entity.HasIndex(membership => new { membership.OrganizationId, membership.Status });
        entity.HasIndex(membership => new { membership.UserId, membership.Status });
        entity.HasIndex(membership => new { membership.InvitedEmail, membership.Status });
        entity.HasIndex(membership => new { membership.UserId, membership.IsDefault });

        // One active membership per user per org
        entity
            .HasIndex(membership => new { membership.OrganizationId, membership.UserId })
            .IsUnique()
            .HasFilter("user_id IS NOT NULL AND status = 'Active'");

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(membership => membership.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TeamMembershipConfiguration : IEntityTypeConfiguration<TeamMembership>
{
    public void Configure(EntityTypeBuilder<TeamMembership> entity)
    {
        entity.ToTable("core_team_memberships");
        entity.HasKey(membership => new
        {
            membership.OrganizationId,
            membership.TeamId,
            membership.UserId,
        });
        entity.Property(membership => membership.OrganizationId).HasMaxLength(128);
        entity.Property(membership => membership.TeamId).HasMaxLength(128);
        entity.Property(membership => membership.UserId).HasMaxLength(128);
        entity.Property(membership => membership.Role).IsRequired().HasMaxLength(64);
        entity.Property(membership => membership.CreatedByUserId).IsRequired().HasMaxLength(128);
        entity.Property(membership => membership.CreatedAtUtc).IsRequired();
        entity.HasIndex(membership => new { membership.UserId, membership.OrganizationId });
        entity.HasIndex(membership => new
        {
            membership.OrganizationId,
            membership.TeamId,
            membership.DisabledAtUtc,
        });
        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(membership => new { membership.OrganizationId, membership.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(membership => membership.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PartitionConfiguration : IEntityTypeConfiguration<Partition>
{
    public void Configure(EntityTypeBuilder<Partition> entity)
    {
        entity.ToTable("core_partitions");
        entity.HasKey(partition => partition.Id);
        entity.Property(partition => partition.Id).HasMaxLength(128);
        entity.Property(partition => partition.ScopeType).IsRequired().HasMaxLength(64);
        entity.Property(partition => partition.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(partition => partition.TeamId).HasMaxLength(128);
        entity.Property(partition => partition.DisplayName).IsRequired().HasMaxLength(200);
        entity.Property(partition => partition.CreatedAtUtc).IsRequired();
        entity.HasIndex(partition => partition.DisabledAtUtc);
        entity.HasIndex(partition => new
        {
            partition.ScopeType,
            partition.OrganizationId,
            partition.TeamId,
        });
        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(partition => partition.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(partition => new { partition.OrganizationId, partition.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
