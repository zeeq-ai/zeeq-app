using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeReviewEstimatedCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cost_catalog_version",
                schema: "zeeq",
                table: "code_review_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_cost_usd",
                schema: "zeeq",
                table: "code_review_records",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_catalog_version",
                schema: "zeeq",
                table: "code_review_records");

            migrationBuilder.DropColumn(
                name: "estimated_cost_usd",
                schema: "zeeq",
                table: "code_review_records");
        }
    }
}
