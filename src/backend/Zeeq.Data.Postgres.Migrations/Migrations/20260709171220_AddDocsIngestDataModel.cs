using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddDocsIngestDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_origin",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .Annotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_cron", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_partman", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_cron", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_partman", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<string>(
                name: "sync_run_id",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "exclude_filters",
                schema: "zeeq",
                table: "docs_libraries",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "include_filters",
                schema: "zeeq",
                table: "docs_libraries",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<DateTimeOffset[]>(
                name: "manual_trigger_history",
                schema: "zeeq",
                table: "docs_libraries",
                type: "timestamptz[]",
                nullable: false,
                defaultValue: new DateTimeOffset[0]);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_sync_at",
                schema: "zeeq",
                table: "docs_libraries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_source_id",
                schema: "zeeq",
                table: "docs_libraries",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "source_default_exclude_filters",
                schema: "zeeq",
                table: "docs_libraries",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "source_default_include_filters",
                schema: "zeeq",
                table: "docs_libraries",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "source_kind",
                schema: "zeeq",
                table: "docs_libraries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_repo_url",
                schema: "zeeq",
                table: "docs_libraries",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "source_synced_at",
                schema: "zeeq",
                table: "docs_libraries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_status",
                schema: "zeeq",
                table: "docs_libraries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "docs_ingest_runs",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    repo_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    public_source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    library_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    root_trace_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    files_total = table.Column<int>(type: "integer", nullable: false),
                    files_added = table.Column<int>(type: "integer", nullable: false),
                    files_updated = table.Column<int>(type: "integer", nullable: false),
                    files_moved = table.Column<int>(type: "integer", nullable: false),
                    files_skipped = table.Column<int>(type: "integer", nullable: false),
                    files_deleted = table.Column<int>(type: "integer", nullable: false),
                    files_failed = table.Column<int>(type: "integer", nullable: false),
                    auth_failure = table.Column<bool>(type: "boolean", nullable: false),
                    failure_message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_docs_ingest_runs", x => new { x.id, x.created_at_utc });
                });

            migrationBuilder.CreateTable(
                name: "docs_public_sources",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    repo_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    default_include_filters = table.Column<string[]>(type: "text[]", nullable: false),
                    default_exclude_filters = table.Column<string[]>(type: "text[]", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sync_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    next_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    manual_trigger_history = table.Column<DateTimeOffset[]>(type: "timestamptz[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_docs_public_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "docs_public_documents",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    public_source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    path_reversed = table.Column<string>(type: "text", nullable: false, computedColumnSql: "reverse(path)", stored: true),
                    previous_paths = table.Column<string[]>(type: "text[]", nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    title_normalized = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    keywords = table.Column<string[]>(type: "text[]", nullable: false),
                    headings = table.Column<string[]>(type: "text[]", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "setweight(to_tsvector('english', coalesce(title, '')), 'A') ||\nsetweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(keywords, ' '), '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(zeeq.immutable_array_to_string(headings, ' '), '')), 'C') ||\nsetweight(to_tsvector('english', coalesce(content, '')), 'D')", stored: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sync_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_docs_public_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_docs_public_documents_docs_public_sources_public_source_id",
                        column: x => x.public_source_id,
                        principalSchema: "zeeq",
                        principalTable: "docs_public_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_organization_id_library_id_sync_run_",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "library_id", "sync_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_libraries_public_source_id",
                schema: "zeeq",
                table: "docs_libraries",
                column: "public_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_docs_libraries_sync_status_next_sync_at",
                schema: "zeeq",
                table: "docs_libraries",
                columns: new[] { "sync_status", "next_sync_at" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_ingest_runs_organization_id_library_id_created_at_utc",
                schema: "zeeq",
                table: "docs_ingest_runs",
                columns: new[] { "organization_id", "library_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_ingest_runs_public_source_id_created_at_utc",
                schema: "zeeq",
                table: "docs_ingest_runs",
                columns: new[] { "public_source_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_ingest_runs_source_kind_auth_failure_created_at_utc",
                schema: "zeeq",
                table: "docs_ingest_runs",
                columns: new[] { "source_kind", "auth_failure", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_documents_public_source_id_content_hash",
                schema: "zeeq",
                table: "docs_public_documents",
                columns: new[] { "public_source_id", "content_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_documents_public_source_id_path",
                schema: "zeeq",
                table: "docs_public_documents",
                columns: new[] { "public_source_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_documents_public_source_id_sync_run_id",
                schema: "zeeq",
                table: "docs_public_documents",
                columns: new[] { "public_source_id", "sync_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_sources_repo_url",
                schema: "zeeq",
                table: "docs_public_sources",
                column: "repo_url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_sources_status_sync_status_next_sync_at",
                schema: "zeeq",
                table: "docs_public_sources",
                columns: new[] { "status", "sync_status", "next_sync_at" });

            // ─── Advanced indexes for docs_public_documents ──────────────────────
            // Copy-on-write GIN, trigram, and text_pattern_ops indexes that lead
            // with (public_source_id) cannot be expressed in the fluent API. Mirror
            // the pattern from docs_library_documents.

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS ix_docs_public_documents_search
                    ON zeeq.docs_public_documents
                    USING GIN (public_source_id, search_vector);

                CREATE INDEX IF NOT EXISTS ix_docs_public_documents_path_reverse
                    ON zeeq.docs_public_documents (public_source_id, path_reversed text_pattern_ops);

                CREATE INDEX IF NOT EXISTS ix_docs_public_documents_title_trgm
                    ON zeeq.docs_public_documents
                    USING GIN (public_source_id, title_normalized gin_trgm_ops);

                CREATE INDEX IF NOT EXISTS ix_docs_public_documents_previous_paths
                    ON zeeq.docs_public_documents
                    USING GIN (public_source_id, previous_paths);
                """
            );

            // btree_gin extension required by the composite GIN indexes above.
            migrationBuilder.Sql(
                """
                CREATE EXTENSION IF NOT EXISTS btree_gin;
                """
            );

            // ─── Partition docs_ingest_runs by created_at_utc ──────────────────
            // EF Core cannot express PARTITION BY RANGE, so we drop the initial
            // table and recreate it with the partitioning clause. This copies the
            // pattern from code_review_records
            // (20260621234152_AddGitHubCodeReviewStorage.cs).
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS zeeq.docs_ingest_runs;

                CREATE TABLE zeeq.docs_ingest_runs (
                    id character varying(128) NOT NULL,
                    created_at_utc timestamp with time zone NOT NULL,
                    source_kind character varying(32) NOT NULL,
                    repo_url character varying(2048) NOT NULL,
                    public_source_id character varying(128),
                    organization_id character varying(128),
                    library_id character varying(128),
                    trigger character varying(16) NOT NULL,
                    status character varying(32) NOT NULL,
                    root_trace_id character varying(64),
                    files_total integer NOT NULL,
                    files_added integer NOT NULL,
                    files_updated integer NOT NULL,
                    files_moved integer NOT NULL,
                    files_skipped integer NOT NULL,
                    files_deleted integer NOT NULL,
                    files_failed integer NOT NULL,
                    auth_failure boolean NOT NULL,
                    failure_message character varying(4096),
                    started_at_utc timestamp with time zone,
                    completed_at_utc timestamp with time zone,
                    updated_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT pk_docs_ingest_runs PRIMARY KEY (id, created_at_utc)
                ) PARTITION BY RANGE (created_at_utc);
                """
            );

            // Recreate the indexes that were on the EF-generated table.
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS ix_docs_ingest_runs_organization_id_library_id_created_at_utc
                    ON zeeq.docs_ingest_runs (organization_id, library_id, created_at_utc);

                CREATE INDEX IF NOT EXISTS ix_docs_ingest_runs_public_source_id_created_at_utc
                    ON zeeq.docs_ingest_runs (public_source_id, created_at_utc);

                CREATE INDEX IF NOT EXISTS ix_docs_ingest_runs_source_kind_auth_failure_created_at_utc
                    ON zeeq.docs_ingest_runs (source_kind, auth_failure, created_at_utc);
                """
            );

            // Configure pg_partman to manage child partitions (14-day intervals,
            // 4 premake, infinite_time_partitions). The hourly maintenance job
            // already exists (20260626194146_SchedulePgPartmanMaintenance.cs).
            //
            // Resolves create_parent()'s actual schema dynamically rather than
            // hardcoding "zeeq." — same pattern as
            // 20260621234152_AddGitHubCodeReviewStorage.cs. The hardcoded
            // zeeq.create_parent(...) call this replaced failed on the
            // production deploy with "function zeeq.create_parent(...) does
            // not exist" (42883): pg_partman was installed there without
            // landing its functions in the zeeq schema, so the unqualified
            // call never resolved. Also guards against re-invocation — a
            // second create_parent() call for an already-managed table is a
            // pg_partman error, which would otherwise surface if this
            // migration is ever retried after a partial prior application left
            // part_config populated but this migration's history row
            // uncommitted.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    partman_schema name;
                    already_registered boolean;
                BEGIN
                    SELECT schema.nspname
                    INTO partman_schema
                    FROM pg_proc proc
                    JOIN pg_namespace schema ON schema.oid = proc.pronamespace
                    WHERE proc.proname = 'create_parent'
                    ORDER BY (schema.nspname = 'partman') DESC, schema.nspname
                    LIMIT 1;

                    IF partman_schema IS NULL THEN
                        RAISE EXCEPTION 'pg_partman create_parent() was not found.';
                    END IF;

                    EXECUTE format(
                        'SELECT EXISTS (SELECT 1 FROM %I.part_config WHERE parent_table = %L)',
                        partman_schema,
                        'zeeq.docs_ingest_runs'
                    ) INTO already_registered;

                    IF NOT already_registered THEN
                        EXECUTE format(
                            'SELECT %I.create_parent(
                                p_parent_table := %L,
                                p_control := %L,
                                p_interval := %L,
                                p_type := %L,
                                p_epoch := %L,
                                p_premake := %s,
                                p_start_partition := %L
                            )',
                            partman_schema,
                            'zeeq.docs_ingest_runs',
                            'created_at_utc',
                            '14 days',
                            'range',
                            'none',
                            4,
                            (date_trunc('day', now()) - interval '14 days')::text
                        );
                    END IF;

                    EXECUTE format(
                        'UPDATE %I.part_config
                         SET infinite_time_partitions = true
                         WHERE parent_table = %L',
                        partman_schema,
                        'zeeq.docs_ingest_runs'
                    );
                END $$;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "docs_ingest_runs",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "docs_public_documents",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "docs_public_sources",
                schema: "zeeq");

            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_organization_id_library_id_sync_run_",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropIndex(
                name: "ix_docs_libraries_public_source_id",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropIndex(
                name: "ix_docs_libraries_sync_status_next_sync_at",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "sync_run_id",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "exclude_filters",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "include_filters",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "manual_trigger_history",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "next_sync_at",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "public_source_id",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "source_default_exclude_filters",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "source_default_include_filters",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "source_kind",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "source_repo_url",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "source_synced_at",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.DropColumn(
                name: "sync_status",
                schema: "zeeq",
                table: "docs_libraries");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_cron", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_partman", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_cron", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_partman", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<string>(
                name: "source_origin",
                schema: "zeeq",
                table: "docs_libraries",
                type: "jsonb",
                nullable: true);
        }
    }
}
