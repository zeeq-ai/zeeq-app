using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSameDomainOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auto_invite_default_role",
                schema: "zeeq",
                table: "core_organizations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "member");

            migrationBuilder.AddColumn<string>(
                name: "auto_invite_same_domain",
                schema: "zeeq",
                table: "core_organizations",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "auto_invite_same_domain_enabled",
                schema: "zeeq",
                table: "core_organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_core_organizations_auto_invite_same_domain",
                schema: "zeeq",
                table: "core_organizations",
                column: "auto_invite_same_domain",
                unique: true,
                filter: "auto_invite_same_domain_enabled = true AND auto_invite_same_domain IS NOT NULL AND disabled_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_core_organizations_auto_invite_same_domain",
                schema: "zeeq",
                table: "core_organizations");

            migrationBuilder.DropColumn(
                name: "auto_invite_default_role",
                schema: "zeeq",
                table: "core_organizations");

            migrationBuilder.DropColumn(
                name: "auto_invite_same_domain",
                schema: "zeeq",
                table: "core_organizations");

            migrationBuilder.DropColumn(
                name: "auto_invite_same_domain_enabled",
                schema: "zeeq",
                table: "core_organizations");
        }
    }
}
