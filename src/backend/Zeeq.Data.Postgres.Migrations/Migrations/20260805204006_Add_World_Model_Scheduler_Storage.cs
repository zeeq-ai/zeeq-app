using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Add_World_Model_Scheduler_Storage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "awm_scheduler_queue_state",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    bucket = table.Column<int>(type: "integer", nullable: false),
                    deficit = table.Column<int>(type: "integer", nullable: false),
                    active_target_count = table.Column<int>(type: "integer", nullable: false),
                    last_visited_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_awm_scheduler_queue_state", x => new { x.organization_id, x.tier, x.bucket });
                });

            migrationBuilder.CreateTable(
                name: "awm_scheduler_pending_targets",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    consumer = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    target_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    bucket = table.Column<int>(type: "integer", nullable: false),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost = table.Column<int>(type: "integer", nullable: false),
                    oldest_event_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    newest_event_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    aggregate_revision = table.Column<long>(type: "bigint", nullable: false),
                    leased_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_awm_scheduler_pending_targets", x => new { x.organization_id, x.consumer, x.target_id });
                    table.ForeignKey(
                        name: "fk_awm_scheduler_pending_targets_awm_scheduler_queue_state_org",
                        columns: x => new { x.organization_id, x.tier, x.bucket },
                        principalSchema: "zeeq",
                        principalTable: "awm_scheduler_queue_state",
                        principalColumns: new[] { "organization_id", "tier", "bucket" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_awm_scheduler_pending_targets_available",
                schema: "zeeq",
                table: "awm_scheduler_pending_targets",
                columns: new[] { "organization_id", "tier", "bucket", "oldest_event_at_utc", "consumer", "target_id" },
                filter: "leased_by IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_awm_scheduler_pending_targets_expired_lease",
                schema: "zeeq",
                table: "awm_scheduler_pending_targets",
                column: "lease_expires_at_utc",
                filter: "leased_by IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_awm_scheduler_queue_state_active",
                schema: "zeeq",
                table: "awm_scheduler_queue_state",
                columns: new[] { "tier", "bucket", "last_visited_at_utc", "organization_id" },
                filter: "active_target_count > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "awm_scheduler_pending_targets",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "awm_scheduler_queue_state",
                schema: "zeeq");
        }
    }
}
