using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RenameFindingsPayloadToSourceTelemetryPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: code_review_records is a declaratively partitioned table (range-partitioned by
            // created_at_utc, child partitions managed by pg_partman). A parent RENAME COLUMN in
            // Postgres always cascades to every child partition — Postgres forbids renaming a
            // column on an individual partition — so this root-only rename is complete. No data
            // rewrite/backfill is needed because every existing value is the "{}" sentinel.
            // Verified end-to-end by CodeReviewStoreIntegrationTests, which apply this migration to
            // a partitioned container and round-trip a review row through a child partition.
            migrationBuilder.RenameColumn(
                name: "findings_payload",
                schema: "zeeq",
                table: "code_review_records",
                newName: "source_telemetry_payload");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "source_telemetry_payload",
                schema: "zeeq",
                table: "code_review_records",
                newName: "findings_payload");
        }
    }
}
