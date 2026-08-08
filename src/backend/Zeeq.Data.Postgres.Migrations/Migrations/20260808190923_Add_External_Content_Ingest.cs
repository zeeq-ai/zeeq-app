using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Add_External_Content_Ingest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_path",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.AddColumn<string>(
                name: "source_external_id",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_source",
                schema: "zeeq",
                table: "docs_libraries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_full_resync_at",
                schema: "zeeq",
                table: "docs_libraries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_scope",
                schema: "zeeq",
                table: "docs_ingest_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "docs_external_pending_content_syncs",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    library_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    external_content_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    marked_dirty_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_by_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    claimed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_docs_external_pending_content_syncs", x => new { x.organization_id, x.library_id, x.external_content_id });
                })
                .Annotation("Npgsql:UnloggedTable", true);

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_path",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "library_id", "path" },
                unique: true,
                filter: "source_external_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_source_external_id",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "library_id", "source_external_id" },
                unique: true,
                filter: "source_external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_docs_libraries_sync_status_next_full_resync_at",
                schema: "zeeq",
                table: "docs_libraries",
                columns: new[] { "sync_status", "next_full_resync_at" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_external_pending_content_syncs_claim",
                schema: "zeeq",
                table: "docs_external_pending_content_syncs",
                columns: new[] { "organization_id", "library_id", "claimed_by_run_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "docs_external_pending_content_syncs",
                schema: "zeeq");

            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_path",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_source_external_id",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropIndex(
                name: "ix_docs_libraries_sync_status_next_full_resync_at",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "source_external_id",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "external_source",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "next_full_resync_at",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "sync_scope",
                schema: "zeeq",
                table: "docs_ingest_runs");

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_path",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "library_id", "path" },
                unique: true);
        }
    }
}
