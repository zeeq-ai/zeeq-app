using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RenameCartsToCodeReviewCarts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_carts_organizations_organization_id",
                schema: "zeeq",
                table: "carts");

            migrationBuilder.DropPrimaryKey(
                name: "pk_carts",
                schema: "zeeq",
                table: "carts");

            migrationBuilder.RenameTable(
                name: "carts",
                schema: "zeeq",
                newName: "code_review_carts",
                newSchema: "zeeq");

            migrationBuilder.RenameIndex(
                name: "ix_carts_updated_at_utc",
                schema: "zeeq",
                table: "code_review_carts",
                newName: "ix_code_review_carts_updated_at_utc");

            migrationBuilder.RenameIndex(
                name: "ix_carts_organization_owner",
                schema: "zeeq",
                table: "code_review_carts",
                newName: "ix_code_review_carts_organization_owner");

            migrationBuilder.AddPrimaryKey(
                name: "pk_code_review_carts",
                schema: "zeeq",
                table: "code_review_carts",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_code_review_carts_organizations_organization_id",
                schema: "zeeq",
                table: "code_review_carts",
                column: "organization_id",
                principalSchema: "zeeq",
                principalTable: "core_organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Update the pg_cron retention job to point at the renamed table.
            migrationBuilder.Sql("SELECT cron.unschedule('zeeq_prune_carts');");
            migrationBuilder.Sql(
                """
                SELECT cron.schedule(
                  'zeeq_prune_carts',
                  '0 3 * * *',
                  $$DELETE FROM zeeq.code_review_carts WHERE updated_at_utc < now() - interval '7 days'$$
                );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_code_review_carts_organizations_organization_id",
                schema: "zeeq",
                table: "code_review_carts");

            migrationBuilder.DropPrimaryKey(
                name: "pk_code_review_carts",
                schema: "zeeq",
                table: "code_review_carts");

            migrationBuilder.RenameTable(
                name: "code_review_carts",
                schema: "zeeq",
                newName: "carts",
                newSchema: "zeeq");

            migrationBuilder.RenameIndex(
                name: "ix_code_review_carts_updated_at_utc",
                schema: "zeeq",
                table: "carts",
                newName: "ix_carts_updated_at_utc");

            migrationBuilder.RenameIndex(
                name: "ix_code_review_carts_organization_owner",
                schema: "zeeq",
                table: "carts",
                newName: "ix_carts_organization_owner");

            migrationBuilder.AddPrimaryKey(
                name: "pk_carts",
                schema: "zeeq",
                table: "carts",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_carts_organizations_organization_id",
                schema: "zeeq",
                table: "carts",
                column: "organization_id",
                principalSchema: "zeeq",
                principalTable: "core_organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Revert the pg_cron retention job back to the original table name.
            migrationBuilder.Sql("SELECT cron.unschedule('zeeq_prune_carts');");
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
    }
}
