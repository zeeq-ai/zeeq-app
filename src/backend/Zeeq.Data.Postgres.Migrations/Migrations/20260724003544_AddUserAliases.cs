using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "core_user_aliases",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    normalized_value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_core_user_aliases", x => x.id);
                    table.ForeignKey(
                        name: "fk_core_user_aliases_core_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_core_user_aliases_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_core_user_aliases_organization_id_kind_normalized_value",
                schema: "zeeq",
                table: "core_user_aliases",
                columns: new[] { "organization_id", "kind", "normalized_value" },
                unique: true,
                filter: "disabled_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_core_user_aliases_organization_id_user_id_kind",
                schema: "zeeq",
                table: "core_user_aliases",
                columns: new[] { "organization_id", "user_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_core_user_aliases_user_id",
                schema: "zeeq",
                table: "core_user_aliases",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "core_user_aliases",
                schema: "zeeq");
        }
    }
}
