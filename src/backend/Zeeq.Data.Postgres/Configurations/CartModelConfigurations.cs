using Zeeq.Core.Carts;
using Zeeq.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Zeeq.Data.Postgres.Configurations;

/// <summary>
/// EF Core configuration for the <see cref="Cart"/> entity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unlogged table.</b>  Carts are workflow ephemera, not durable data.  Loss on an
/// unclean shutdown is acceptable.  A <c>pg_cron</c> job (see migration) prunes rows
/// older than seven days.
/// </para>
/// <para>
/// <b>JSON mapping.</b>  The two collection properties use <c>OwnsMany().ToJson()</c>
/// to map to <c>jsonb</c> columns — the same pattern used by every other JSON column
/// in the codebase (e.g. <c>SourceOrigin</c> in <c>DocumentModelConfigurations</c>).
/// <see cref="Cart.ItemSummaries"/> and <see cref="Cart.ItemsPayload"/> must have
/// <c>{ get; set; }</c> with a <see cref="List{T}"/> backing so EF Core can populate
/// them during materialization.
/// </para>
/// </remarks>
internal sealed class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> entity)
    {
        entity.ToTable("code_review_carts");
        entity.IsUnlogged();
        entity.HasKey(cart => cart.Id);

        entity.Property(cart => cart.Id).HasMaxLength(64);
        entity.Property(cart => cart.OrganizationId).IsRequired().HasMaxLength(128);
        entity.Property(cart => cart.TeamId).HasMaxLength(128);
        entity.Property(cart => cart.OwnerUserId).IsRequired().HasMaxLength(128);
        entity.Property(cart => cart.Name).IsRequired().HasMaxLength(64);

        // Lightweight summary JSON array used by startup/list UI.
        entity.OwnsMany(
            cart => cart.ItemSummaries,
            summaries =>
            {
                summaries.ToJson("item_summaries");
                summaries.Property(item => item.Hash).IsRequired();
                summaries.Property(item => item.Title).IsRequired();
                summaries.Property(item => item.Facet).IsRequired();
                summaries.Property(item => item.Summary).IsRequired();
                summaries.Property(item => item.Criticality).IsRequired();
            }
        );

        // Full finding payload JSON array used for MCP/text/copy-source.
        entity.OwnsMany(
            cart => cart.ItemsPayload,
            payload =>
            {
                payload.ToJson("items_payload");
                payload.Property(item => item.Hash).IsRequired();
                payload.Property(item => item.Title).IsRequired();
                payload.Property(item => item.Criticality).IsRequired();
            }
        );

        entity
            .HasOne<Organization>()
            .WithMany()
            .HasForeignKey(cart => cart.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Primary lookup path for ListForOwnerAsync and the 5-cart cap count.
        entity
            .HasIndex(cart => new { cart.OrganizationId, cart.OwnerUserId })
            .HasDatabaseName("ix_code_review_carts_organization_owner");

        // Supports the seven-day retention cleanup query.
        entity.HasIndex(cart => cart.UpdatedAtUtc).HasDatabaseName("ix_code_review_carts_updated_at_utc");
    }
}
