using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubAppInstallations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_cron", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_partman", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "code_review_github_app_installations",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    account_login = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_id = table.Column<long>(type: "bigint", nullable: false),
                    account_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    repository_selection = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    installed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    suspended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    raw_installation_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_github_app_installations", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_review_github_app_installations_organizations_organiza",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_github_app_installations_teams_organization_id_",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_app_installations_account_login_account_",
                schema: "zeeq",
                table: "code_review_github_app_installations",
                columns: new[] { "account_login", "account_type" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_app_installations_installation_id",
                schema: "zeeq",
                table: "code_review_github_app_installations",
                column: "installation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_app_installations_organization_id_team_i",
                schema: "zeeq",
                table: "code_review_github_app_installations",
                columns: new[] { "organization_id", "team_id", "disabled_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_review_github_app_installations",
                schema: "zeeq");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_cron", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_partman", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
