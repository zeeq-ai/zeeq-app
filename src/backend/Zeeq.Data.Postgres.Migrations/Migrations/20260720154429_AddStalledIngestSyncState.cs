using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddStalledIngestSyncState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "active_sync_run_created_at_utc",
                schema: "zeeq",
                table: "docs_public_sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "active_sync_run_id",
                schema: "zeeq",
                table: "docs_public_sources",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_public_sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sync_started_at_utc",
                schema: "zeeq",
                table: "docs_public_sources",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "active_sync_run_created_at_utc",
                schema: "zeeq",
                table: "docs_libraries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "active_sync_run_id",
                schema: "zeeq",
                table: "docs_libraries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_libraries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sync_started_at_utc",
                schema: "zeeq",
                table: "docs_libraries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_sources_sync_status_sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_public_sources",
                columns: new[] { "sync_status", "sync_queued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_sources_sync_status_sync_started_at_utc",
                schema: "zeeq",
                table: "docs_public_sources",
                columns: new[] { "sync_status", "sync_started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_libraries_sync_status_sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_libraries",
                columns: new[] { "sync_status", "sync_queued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_libraries_sync_status_sync_started_at_utc",
                schema: "zeeq",
                table: "docs_libraries",
                columns: new[] { "sync_status", "sync_started_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_docs_public_sources_sync_status_sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_public_sources");

            migrationBuilder.DropIndex(
                name: "ix_docs_public_sources_sync_status_sync_started_at_utc",
                schema: "zeeq",
                table: "docs_public_sources");

            migrationBuilder.DropIndex(
                name: "ix_docs_libraries_sync_status_sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropIndex(
                name: "ix_docs_libraries_sync_status_sync_started_at_utc",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "active_sync_run_created_at_utc",
                schema: "zeeq",
                table: "docs_public_sources");

            migrationBuilder.DropColumn(
                name: "active_sync_run_id",
                schema: "zeeq",
                table: "docs_public_sources");

            migrationBuilder.DropColumn(
                name: "sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_public_sources");

            migrationBuilder.DropColumn(
                name: "sync_started_at_utc",
                schema: "zeeq",
                table: "docs_public_sources");

            migrationBuilder.DropColumn(
                name: "active_sync_run_created_at_utc",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "active_sync_run_id",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "sync_queued_at_utc",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "sync_started_at_utc",
                schema: "zeeq",
                table: "docs_libraries");
        }
    }
}
