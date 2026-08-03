using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentConversationRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");

            migrationBuilder.AlterColumn<long>(
                name: "total_output_tokens",
                schema: "zeeq",
                table: "agent_conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<long>(
                name: "total_input_tokens",
                schema: "zeeq",
                table: "agent_conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "missing_cost_completion_count",
                schema: "zeeq",
                table: "agent_conversations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "rollup_version",
                schema: "zeeq",
                table: "agent_conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "title",
                schema: "zeeq",
                table: "agent_conversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_rollup_backfill",
                schema: "zeeq",
                table: "agent_conversations",
                columns: new[] { "rollup_version", "started_at_utc", "organization_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agent_conversations_rollup_backfill",
                schema: "zeeq",
                table: "agent_conversations");

            migrationBuilder.DropColumn(
                name: "missing_cost_completion_count",
                schema: "zeeq",
                table: "agent_conversations");

            migrationBuilder.DropColumn(
                name: "rollup_version",
                schema: "zeeq",
                table: "agent_conversations");

            migrationBuilder.DropColumn(
                name: "title",
                schema: "zeeq",
                table: "agent_conversations");

            migrationBuilder.AlterColumn<int>(
                name: "total_output_tokens",
                schema: "zeeq",
                table: "agent_conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);

            migrationBuilder.AlterColumn<int>(
                name: "total_input_tokens",
                schema: "zeeq",
                table: "agent_conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 0L);
        }
    }
}
