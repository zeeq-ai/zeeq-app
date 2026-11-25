using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeReviewEvaluationFalsePositiveCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "false_positive_checked_case_count",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "repeated_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "tracked_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "repeated_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_case_results",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "tracked_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_case_results",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "false_positive_checked_case_count",
                schema: "zeeq",
                table: "code_review_evaluation_runs");

            migrationBuilder.DropColumn(
                name: "repeated_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_runs");

            migrationBuilder.DropColumn(
                name: "tracked_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_runs");

            migrationBuilder.DropColumn(
                name: "repeated_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_case_results");

            migrationBuilder.DropColumn(
                name: "tracked_false_positive_count",
                schema: "zeeq",
                table: "code_review_evaluation_case_results");
        }
    }
}
