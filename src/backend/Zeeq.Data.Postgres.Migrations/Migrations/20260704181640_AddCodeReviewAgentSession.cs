using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeReviewAgentSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "repository_id",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<string>(
                name: "pull_request_record_id",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<string>(
                name: "agent_session_id",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_review_records_organization_id_agent_session_id_create",
                schema: "zeeq",
                table: "code_review_records",
                columns: new[] { "organization_id", "agent_session_id", "created_at_utc" },
                filter: "agent_session_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_code_review_records_organization_id_agent_session_id_create",
                schema: "zeeq",
                table: "code_review_records");

            migrationBuilder.DropColumn(
                name: "agent_session_id",
                schema: "zeeq",
                table: "code_review_records");

            migrationBuilder.AlterColumn<string>(
                name: "repository_id",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "pull_request_record_id",
                schema: "zeeq",
                table: "code_review_records",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);
        }
    }
}
