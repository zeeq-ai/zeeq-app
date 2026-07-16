using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricEventsStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "zeeq_metric_events",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    metric_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    metric_value = table.Column<double>(type: "double precision", nullable: false),
                    user_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    tool_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    repository_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    library = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    facet = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tags = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_zeeq_metric_events", x => new { x.id, x.created_at_utc });
                });

            // Recreate zeeq_metric_events as a range-partitioned table. EF cannot
            // express PARTITION BY, so drop the plain table it just declared and
            // rebuild it partitioned by created_at_utc, mirroring code_review_records
            // (20260621234152). The composite PK keeps the partition key in the key.
            // pg_partman manages 7-day child partitions with 30-day retention and
            // auto-drop; the hourly cron maintenance (20260626194146) runs premake
            // and retention for every part_config row.
            migrationBuilder.DropTable(name: "zeeq_metric_events", schema: "zeeq");

            migrationBuilder.Sql(
                """
                CREATE TABLE zeeq.zeeq_metric_events (
                    id bigint GENERATED ALWAYS AS IDENTITY,
                    created_at_utc timestamp with time zone NOT NULL,
                    organization_id character varying(128) NOT NULL,
                    team_id character varying(128),
                    metric_type character varying(128) NOT NULL,
                    metric_value double precision NOT NULL,
                    user_email character varying(320),
                    tool_name character varying(128),
                    repository_id character varying(128),
                    library character varying(128),
                    facet character varying(64),
                    tags jsonb NOT NULL,
                    CONSTRAINT pk_zeeq_metric_events PRIMARY KEY (id, created_at_utc)
                ) PARTITION BY RANGE (created_at_utc);

                DO $$
                DECLARE
                    partman_schema name;
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
                        'zeeq.zeeq_metric_events',
                        'created_at_utc',
                        '7 days',
                        'range',
                        'none',
                        2,
                        (date_trunc('day', now()) - interval '7 days')::text
                    );

                    -- Retention: 30-day look back, old partitions dropped outright.
                    -- Unlike code_review_records (which keeps history), metric events
                    -- are loss-tolerant informational telemetry.
                    EXECUTE format(
                        'UPDATE %I.part_config
                         SET infinite_time_partitions = true,
                             retention = %L,
                             retention_keep_table = false,
                             retention_keep_index = false
                         WHERE parent_table = %L',
                        partman_schema,
                        '30 days',
                        'zeeq.zeeq_metric_events'
                    );
                END $$;
                """
            );

            // NOTE: These two code_review_records indexes are introduced by THIS
            // migration — they come from the CodeReviewModelConfigurations change made
            // in the same changeset, and EF batches all pending model changes into one
            // migration. They target the separate, already-partitioned code_review_records
            // table (added in 20260621234152), so they are independent of the
            // zeeq_metric_events partitioning DDL above and their creation order here
            // does not matter. Down() drops exactly these, restoring the prior schema.
            migrationBuilder.CreateIndex(
                name: "ix_code_review_records_organization_id_author_login_created_at",
                schema: "zeeq",
                table: "code_review_records",
                columns: new[] { "organization_id", "author_login", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_records_organization_id_repository_id_created_a",
                schema: "zeeq",
                table: "code_review_records",
                columns: new[] { "organization_id", "repository_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_created_at",
                schema: "zeeq",
                table: "zeeq_metric_events",
                columns: new[] { "organization_id", "metric_type", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_facet_crea",
                schema: "zeeq",
                table: "zeeq_metric_events",
                columns: new[] { "organization_id", "metric_type", "facet", "created_at_utc" },
                filter: "facet IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_library_cr",
                schema: "zeeq",
                table: "zeeq_metric_events",
                columns: new[] { "organization_id", "metric_type", "library", "created_at_utc" },
                filter: "library IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_repository",
                schema: "zeeq",
                table: "zeeq_metric_events",
                columns: new[] { "organization_id", "metric_type", "repository_id", "created_at_utc" },
                filter: "repository_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_tool_name_",
                schema: "zeeq",
                table: "zeeq_metric_events",
                columns: new[] { "organization_id", "metric_type", "tool_name", "created_at_utc" },
                filter: "tool_name IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_user_email",
                schema: "zeeq",
                table: "zeeq_metric_events",
                columns: new[] { "organization_id", "metric_type", "user_email", "created_at_utc" },
                filter: "user_email IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove the pg_partman configuration row before dropping the table so
            // maintenance stops managing partitions for a table that no longer exists.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    partman_schema name;
                BEGIN
                    SELECT schema.nspname
                    INTO partman_schema
                    FROM pg_class cls
                    JOIN pg_namespace schema ON schema.oid = cls.relnamespace
                    WHERE cls.relname = 'part_config'
                    ORDER BY (schema.nspname = 'partman') DESC, schema.nspname
                    LIMIT 1;

                    IF partman_schema IS NOT NULL THEN
                        EXECUTE format(
                            'DELETE FROM %I.part_config WHERE parent_table = %L',
                            partman_schema,
                            'zeeq.zeeq_metric_events'
                        );
                    END IF;
                END $$;
                """
            );

            migrationBuilder.DropTable(
                name: "zeeq_metric_events",
                schema: "zeeq");

            // NOTE: Symmetric with Up(). These two code_review_records indexes were
            // created by this migration (via the same-changeset config edit), so
            // dropping them here restores the exact pre-migration schema — it does not
            // remove any index that predates this migration.
            migrationBuilder.DropIndex(
                name: "ix_code_review_records_organization_id_author_login_created_at",
                schema: "zeeq",
                table: "code_review_records");

            migrationBuilder.DropIndex(
                name: "ix_code_review_records_organization_id_repository_id_created_a",
                schema: "zeeq",
                table: "code_review_records");
        }
    }
}
