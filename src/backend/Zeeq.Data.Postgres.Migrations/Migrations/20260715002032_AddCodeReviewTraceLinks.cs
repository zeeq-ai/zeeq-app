using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeReviewTraceLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "execution_trace_parent",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "execution_trace_state",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_review_id",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_review_records_organization_id_review_group_id_status_",
                schema: "zeeq",
                table: "code_review_records",
                columns: new[] { "organization_id", "review_group_id", "status", "created_at_utc" },
                filter: "review_group_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_code_review_records_organization_id_review_group_id_status_",
                schema: "zeeq",
                table: "code_review_records");

            migrationBuilder.DropColumn(
                name: "execution_trace_parent",
                schema: "zeeq",
                table: "code_review_records");

            migrationBuilder.DropColumn(
                name: "execution_trace_state",
                schema: "zeeq",
                table: "code_review_records");

            migrationBuilder.DropColumn(
                name: "previous_review_id",
                schema: "zeeq",
                table: "code_review_records");
        }
    }
}
