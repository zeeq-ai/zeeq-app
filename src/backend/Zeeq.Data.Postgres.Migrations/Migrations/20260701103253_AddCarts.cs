using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCarts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder
                .CreateTable(
                    name: "carts",
                    schema: "zeeq",
                    columns: table => new
                    {
                        id = table.Column<string>(
                            type: "character varying(64)",
                            maxLength: 64,
                            nullable: false
                        ),
                        organization_id = table.Column<string>(
                            type: "character varying(128)",
                            maxLength: 128,
                            nullable: false
                        ),
                        team_id = table.Column<string>(
                            type: "character varying(128)",
                            maxLength: 128,
                            nullable: true
                        ),
                        owner_user_id = table.Column<string>(
                            type: "character varying(128)",
                            maxLength: 128,
                            nullable: false
                        ),
                        name = table.Column<string>(
                            type: "character varying(64)",
                            maxLength: 64,
                            nullable: false
                        ),
                        created_at_utc = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: false
                        ),
                        saved_at_utc = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: false
                        ),
                        updated_at_utc = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: false
                        ),
                        item_summaries = table.Column<string>(type: "jsonb", nullable: true),
                        items_payload = table.Column<string>(type: "jsonb", nullable: true),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("pk_carts", x => x.id);
                        table.ForeignKey(
                            name: "fk_carts_organizations_organization_id",
                            column: x => x.organization_id,
                            principalSchema: "zeeq",
                            principalTable: "core_organizations",
                            principalColumn: "id",
                            onDelete: ReferentialAction.Restrict
                        );
                    }
                )
                .Annotation("Npgsql:UnloggedTable", true);

            // Belt-and-suspenders: ensure the table is unlogged even if the
            // annotation is not applied by the provider.
            migrationBuilder.Sql("ALTER TABLE zeeq.carts SET UNLOGGED;");

            migrationBuilder.CreateIndex(
                name: "ix_carts_organization_owner",
                schema: "zeeq",
                table: "carts",
                columns: new[] { "organization_id", "owner_user_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_carts_updated_at_utc",
                schema: "zeeq",
                table: "carts",
                column: "updated_at_utc"
            );

            // Prune saved carts older than seven days. Saved carts are immutable,
            // so UpdatedAtUtc equals SavedAtUtc — the cron cleanup uses the same column.
            migrationBuilder.Sql(
                """
                SELECT cron.schedule(
                  'zeeq_prune_carts',
                  '0 3 * * *',
                  $$DELETE FROM zeeq.carts WHERE updated_at_utc < now() - interval '7 days'$$
                );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT cron.unschedule('zeeq_prune_carts');");
            migrationBuilder.DropTable(name: "carts", schema: "zeeq");
        }
    }
}
