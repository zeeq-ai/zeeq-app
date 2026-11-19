using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "zeeq",
                table: "core_teams",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalized_name",
                schema: "zeeq",
                table: "core_teams",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                computedColumnSql: "lower(btrim(display_name))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_core_teams_active_directory",
                schema: "zeeq",
                table: "core_teams",
                columns: new[] { "organization_id", "id" },
                filter: "NOT is_root_team AND disabled_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_core_teams_active_name",
                schema: "zeeq",
                table: "core_teams",
                columns: new[] { "organization_id", "normalized_name" },
                unique: true,
                filter: "NOT is_root_team AND disabled_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_core_teams_active_root",
                schema: "zeeq",
                table: "core_teams",
                column: "organization_id",
                unique: true,
                filter: "is_root_team AND disabled_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_core_team_memberships_organization_id_user_id_team_id",
                schema: "zeeq",
                table: "core_team_memberships",
                columns: new[] { "organization_id", "user_id", "team_id" },
                filter: "disabled_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_core_teams_active_directory",
                schema: "zeeq",
                table: "core_teams");

            migrationBuilder.DropIndex(
                name: "ux_core_teams_active_name",
                schema: "zeeq",
                table: "core_teams");

            migrationBuilder.DropIndex(
                name: "ux_core_teams_active_root",
                schema: "zeeq",
                table: "core_teams");

            migrationBuilder.DropIndex(
                name: "ix_core_team_memberships_organization_id_user_id_team_id",
                schema: "zeeq",
                table: "core_team_memberships");

            migrationBuilder.DropColumn(
                name: "normalized_name",
                schema: "zeeq",
                table: "core_teams");

            migrationBuilder.DropColumn(
                name: "description",
                schema: "zeeq",
                table: "core_teams");
        }
    }
}
