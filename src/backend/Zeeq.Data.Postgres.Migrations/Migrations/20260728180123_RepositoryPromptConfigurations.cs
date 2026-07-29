using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryPromptConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_code_review_repositories_organization_id_id",
                schema: "zeeq",
                table: "code_review_repositories",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateTable(
                name: "code_repository_prompt_configurations",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    repository_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    library_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    placeholder_values = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_repository_prompt_configurations", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_repository_prompt_configurations_code_review_repositor",
                        columns: x => new { x.organization_id, x.repository_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_code_repository_prompt_configurations_organizations_organiz",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_repository_prompt_configurations_organization_id_repos",
                schema: "zeeq",
                table: "code_repository_prompt_configurations",
                columns: new[] { "organization_id", "repository_id", "library_id", "document_id" },
                unique: true,
                filter: "disabled_at_utc IS NULL");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_repository_prompt_configurations",
                schema: "zeeq");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_code_review_repositories_organization_id_id",
                schema: "zeeq",
                table: "code_review_repositories");
        }
    }
}
