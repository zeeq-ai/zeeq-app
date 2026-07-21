using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSameDomainInvitationProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_same_domain_auto_invite",
                schema: "zeeq",
                table: "core_organization_memberships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_same_domain_auto_invite",
                schema: "zeeq",
                table: "core_organization_memberships",
                columns: new[] { "organization_id", "invited_email" },
                unique: true,
                filter: "is_same_domain_auto_invite = true AND invited_email IS NOT NULL AND status = 'Pending' AND disabled_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_core_organization_memberships_same_domain_auto_invite",
                schema: "zeeq",
                table: "core_organization_memberships");

            migrationBuilder.DropColumn(
                name: "is_same_domain_auto_invite",
                schema: "zeeq",
                table: "core_organization_memberships");
        }
    }
}
