using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryTeamVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "docs_library_team_grants",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", nullable: false),
                    library_id = table.Column<string>(type: "character varying(128)", nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_docs_library_team_grants", x => new { x.organization_id, x.library_id, x.team_id });
                    table.ForeignKey(
                        name: "fk_docs_library_team_grants_docs_libraries_organization_id_lib",
                        columns: x => new { x.organization_id, x.library_id },
                        principalSchema: "zeeq",
                        principalTable: "docs_libraries",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_docs_library_team_grants_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_team_grants_organization_id_team_id",
                schema: "zeeq",
                table: "docs_library_team_grants",
                columns: new[] { "organization_id", "team_id" });
            // The root team represented organization-wide access before explicit grants.
            migrationBuilder.Sql(
                """
                INSERT INTO zeeq.docs_library_team_grants (organization_id, library_id, team_id)
                SELECT library.organization_id, library.id, team.id
                FROM zeeq.docs_libraries AS library
                JOIN zeeq.core_teams AS team
                    ON team.organization_id = library.organization_id AND team.id = library.team_id
                WHERE NOT team.is_root_team
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "docs_library_team_grants",
                schema: "zeeq");
        }
    }
}
