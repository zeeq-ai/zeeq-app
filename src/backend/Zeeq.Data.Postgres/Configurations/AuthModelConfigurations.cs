using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

internal sealed class ExternalUserIdentityConfiguration
    : IEntityTypeConfiguration<ExternalUserIdentity>
{
    public void Configure(EntityTypeBuilder<ExternalUserIdentity> entity)
    {
        entity.ToTable("auth_user_identities");
        entity.HasKey(identity => new { identity.Provider, identity.ProviderSubject });
        entity.Property(identity => identity.UserId).IsRequired().HasMaxLength(128);
        entity.Property(identity => identity.Provider).IsRequired().HasMaxLength(128);
        entity.Property(identity => identity.ProviderSubject).IsRequired().HasMaxLength(512);
        entity.Property(identity => identity.Email).HasMaxLength(320);
        entity.Property(identity => identity.DisplayName).HasMaxLength(200);
        entity.Property(identity => identity.PictureUrl).HasMaxLength(2048);
        entity.HasIndex(identity => identity.UserId);
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(identity => identity.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ClientCredentialConfiguration : IEntityTypeConfiguration<ClientCredential>
{
    public void Configure(EntityTypeBuilder<ClientCredential> entity)
    {
        entity.ToTable("auth_client_credentials");
        entity.HasKey(credential => credential.ClientId);
        entity.Property(credential => credential.ClientId).HasMaxLength(128);
        entity.Property(credential => credential.OwnerUserId).IsRequired().HasMaxLength(128);
        entity.Property(credential => credential.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(credential => credential.TeamId).IsRequired().HasMaxLength(128);
        entity.Property(credential => credential.OwnerProvider).IsRequired().HasMaxLength(128);
        entity
            .Property(credential => credential.OwnerProviderSubject)
            .IsRequired()
            .HasMaxLength(512);
        entity.Property(credential => credential.DisplayName).IsRequired().HasMaxLength(200);
        entity.Property(credential => credential.ClientSecret).IsRequired().HasMaxLength(512);
        entity.Property(credential => credential.SelectedPartitionIdsJson).IsRequired();
        entity.Property(credential => credential.CreatedAtUtc).IsRequired();
        entity.HasIndex(credential => credential.OwnerUserId);
        entity.HasIndex(credential => new
        {
            credential.OrganizationId,
            credential.TeamId,
            credential.OwnerUserId,
        });
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(credential => credential.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(credential => credential.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(credential => new { credential.OrganizationId, credential.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DcrClientSetupConfiguration : IEntityTypeConfiguration<DcrClientSetup>
{
    public void Configure(EntityTypeBuilder<DcrClientSetup> entity)
    {
        entity.ToTable("auth_dcr_client_setups");
        entity.HasKey(setup => setup.ClientId);
        entity.Property(setup => setup.ClientId).HasMaxLength(128);
        entity.Property(setup => setup.Status).IsRequired().HasMaxLength(64);
        entity.Property(setup => setup.ClientName).IsRequired().HasMaxLength(200);
        entity.Property(setup => setup.RedirectUrisJson).IsRequired();
        entity.Property(setup => setup.RequestedScopes).IsRequired().HasMaxLength(1024);
        entity.Property(setup => setup.CreatedAtUtc).IsRequired();
        entity.Property(setup => setup.ExpiresAtUtc).IsRequired();
        entity.Property(setup => setup.ClaimedUserId).HasMaxLength(128);
        entity.Property(setup => setup.OrganizationId).HasMaxLength(128);
        entity.Property(setup => setup.TeamId).HasMaxLength(128);
        entity.Property(setup => setup.SelectedPartitionIdsJson).IsRequired();
        entity.Property(setup => setup.ClaimedOwnerProvider).HasMaxLength(128);
        entity.Property(setup => setup.ClaimedOwnerProviderSubject).HasMaxLength(512);
        entity.HasIndex(setup => new { setup.Status, setup.ExpiresAtUtc });
        entity.HasIndex(setup => setup.ClaimedUserId);
        entity.HasIndex(setup => new
        {
            setup.OrganizationId,
            setup.TeamId,
            setup.ClaimedUserId,
        });
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(setup => setup.ClaimedUserId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(setup => setup.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(setup => new { setup.OrganizationId, setup.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
{
    public void Configure(EntityTypeBuilder<UserToken> entity)
    {
        entity.ToTable("auth_user_tokens");
        entity.HasKey(token => token.Id);
        entity.Property(token => token.Id).HasMaxLength(128);
        entity.Property(token => token.OwnerUserId).IsRequired().HasMaxLength(128);
        entity.Property(token => token.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(token => token.TeamId).IsRequired().HasMaxLength(128);
        entity.Property(token => token.OwnerProvider).IsRequired().HasMaxLength(128);
        entity.Property(token => token.OwnerProviderSubject).IsRequired().HasMaxLength(512);
        entity.Property(token => token.DisplayName).IsRequired().HasMaxLength(200);
        entity.Property(token => token.SelectedPartitionIdsJson).IsRequired();
        entity.Property(token => token.CreatedAtUtc).IsRequired();
        entity.Property(token => token.ExpiresAtUtc).IsRequired();
        entity.HasIndex(token => token.OwnerUserId);
        entity.HasIndex(token => new
        {
            token.OrganizationId,
            token.TeamId,
            token.OwnerUserId,
        });
        entity
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(token => token.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        entity
            .HasOne<Team>()
            .WithMany()
            .HasPrincipalKey(team => new { team.OrganizationId, team.Id })
            .HasForeignKey(token => new { token.OrganizationId, token.TeamId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuthTransientStateConfiguration : IEntityTypeConfiguration<AuthTransientState>
{
    public void Configure(EntityTypeBuilder<AuthTransientState> entity)
    {
        entity.ToTable("auth_transient_states");
        entity.HasKey(state => new { state.Purpose, state.Key });
        entity.Property(state => state.Key).HasMaxLength(256);
        entity.Property(state => state.Purpose).HasMaxLength(64);
        entity.Property(state => state.PayloadJson).IsRequired();
        entity.Property(state => state.ExpiresAtUtc).IsRequired();
        entity.HasIndex(state => new
        {
            state.Purpose,
            state.ExpiresAtUtc,
            state.ConsumedAtUtc,
        });
    }
}
