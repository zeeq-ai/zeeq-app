using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class MoveCodeReviewSettingsToOrganizationJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "code_review_organization_settings", schema: "zeeq");

            migrationBuilder.AddColumn<string>(
                name: "code_review_configuration",
                schema: "zeeq",
                table: "core_organizations",
                type: "jsonb",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code_review_configuration",
                schema: "zeeq",
                table: "core_organizations"
            );

            migrationBuilder.CreateTable(
                name: "code_review_organization_settings",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    disabled_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    execution_lease_duration = table.Column<TimeSpan>(
                        type: "interval",
                        nullable: false
                    ),
                    max_concurrent_reviews = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_organization_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_review_organization_settings_organizations_organizatio",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_organization_settings_organization_id",
                schema: "zeeq",
                table: "code_review_organization_settings",
                column: "organization_id",
                unique: true
            );
        }
    }
}
