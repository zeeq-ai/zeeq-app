using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Telemetry_V2_UnifiedEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // agent_conversations — non-partitioned root table.
            migrationBuilder.CreateTable(
                name: "agent_conversations",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    harness = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    harness_variant = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    repo_remote_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    head_branch = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    total_input_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    total_output_tokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    total_cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: true),
                    owner_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    created_by_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ownership_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    soft_delete_metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_conversations", x => new { x.organization_id, x.id });
                });

            // agent_pull_request_session_links — non-partitioned many-to-many.
            migrationBuilder.CreateTable(
                name: "agent_pull_request_session_links",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    pull_request_record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    conversation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    link_origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    linked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    linked_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_pending = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_pull_request_session_links", x => x.id);
                });

            // agent_session_events — EF creates it first (non-partitioned), then we
            // drop and recreate as PARTITION BY RANGE with pg_partman below.
            migrationBuilder.CreateTable(
                name: "agent_session_events",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    conversation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_sequence = table.Column<long>(type: "bigint", nullable: true),
                    source_record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    event_type = table.Column<byte>(type: "smallint", nullable: false),
                    prompt_group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    tool_call_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    provider_request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    prompt_text = table.Column<string>(type: "text", nullable: true),
                    prompt_length = table.Column<int>(type: "integer", nullable: true),
                    tool_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    tool_name_raw = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    mcp_server = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    mcp_server_origin = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    mcp_server_scope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    arguments_json = table.Column<string>(type: "jsonb", nullable: true),
                    output_snippet = table.Column<string>(type: "text", nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    decision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    decision_source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    cached_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    reasoning_tokens = table.Column<int>(type: "integer", nullable: true),
                    tool_tokens = table.Column<int>(type: "integer", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric(14,6)", nullable: true),
                    cost_source = table.Column<byte>(type: "smallint", nullable: true),
                    cost_units_raw = table.Column<long>(type: "bigint", nullable: true),
                    query_source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_housekeeping = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_session_events", x => new { x.id, x.occurred_at_utc });
                });

            // telemetry_raw_requests — UNLOGGED transient table.
            migrationBuilder.CreateTable(
                name: "telemetry_raw_requests",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signal_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ingest_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ingest_organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    harness_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    processing_lease_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    processing_lease_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    effective_obfuscation_policy_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    quarantine_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    quarantined_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry_raw_requests", x => x.id);
                });

            // --- Hand-edited partitioning DDL for agent_session_events ---

            // Make telemetry_raw_requests UNLOGGED (acceptable WAL tradeoff for transient data).
            migrationBuilder.Sql("ALTER TABLE zeeq.telemetry_raw_requests SET UNLOGGED;");

            // Drop the non-partitioned table EF created above.
            migrationBuilder.Sql("DROP TABLE zeeq.agent_session_events;");

            // Recreate as PARTITION BY RANGE with the same column shape.
            migrationBuilder.Sql(
                """
                CREATE TABLE zeeq.agent_session_events (
                    id character varying(128) NOT NULL,
                    occurred_at_utc timestamp with time zone NOT NULL,
                    source_sequence bigint,
                    source_record_id character varying(128),
                    organization_id character varying(128) NOT NULL,
                    conversation_id character varying(128) NOT NULL,
                    event_type smallint NOT NULL,
                    prompt_group_id character varying(128),
                    tool_call_id character varying(128),
                    provider_request_id character varying(128),
                    prompt_text text,
                    prompt_length integer,
                    tool_name character varying(512),
                    tool_name_raw character varying(512),
                    mcp_server character varying(256),
                    mcp_server_origin character varying(256),
                    mcp_server_scope character varying(256),
                    arguments_json jsonb,
                    output_snippet text,
                    success boolean,
                    duration_ms integer,
                    decision character varying(64),
                    decision_source character varying(128),
                    model character varying(128),
                    input_tokens integer,
                    cached_tokens integer,
                    output_tokens integer,
                    reasoning_tokens integer,
                    tool_tokens integer,
                    cost_usd numeric(14,6),
                    cost_source smallint,
                    cost_units_raw bigint,
                    query_source character varying(256),
                    is_housekeeping boolean NOT NULL DEFAULT false,
                    CONSTRAINT pk_agent_session_events PRIMARY KEY (id, occurred_at_utc)
                ) PARTITION BY RANGE (occurred_at_utc);
                """
            );

            // Initialize partitions via pg_partman create_parent().
            migrationBuilder.Sql(
                """
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
                        'zeeq.agent_session_events',
                        'occurred_at_utc',
                        '14 days',
                        'range',
                        'none',
                        4,
                        (date_trunc('day', now()) - interval '14 days')::text
                    );

                    EXECUTE format(
                        'UPDATE %I.part_config
                         SET infinite_time_partitions = true
                         WHERE parent_table = %L',
                        partman_schema,
                        'zeeq.agent_session_events'
                    );
                END $$;
                """
            );

            // Indexes on the parent (inherited by child partitions).
            migrationBuilder.CreateIndex(
                name: "ix_agent_session_events_organization_id_conversation_id_occurr",
                schema: "zeeq",
                table: "agent_session_events",
                columns: new[] { "organization_id", "conversation_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_session_events_organization_id_event_type_occurred_at",
                schema: "zeeq",
                table: "agent_session_events",
                columns: new[] { "organization_id", "event_type", "occurred_at_utc" },
                filter: "event_type IN (2, 3)");

            // BRIN index for time-series scans.
            migrationBuilder.Sql(
                "CREATE INDEX ix_agent_session_events_occurred_at_utc_brin ON zeeq.agent_session_events USING BRIN (occurred_at_utc);"
            );

            // Indexes for agent_conversations, agent_pull_request_session_links,
            // and telemetry_raw_requests (non-partitioned, not dropped).
            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_organization_id_harness_started_at_utc",
                schema: "zeeq",
                table: "agent_conversations",
                columns: new[] { "organization_id", "harness", "started_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_organization_id_owner_email",
                schema: "zeeq",
                table: "agent_conversations",
                columns: new[] { "organization_id", "owner_email" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_organization_id_head_branch_repo_remote",
                schema: "zeeq",
                table: "agent_conversations",
                columns: new[] { "organization_id", "head_branch", "repo_remote_url" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_pull_request_session_links_organization_id_conversati",
                schema: "zeeq",
                table: "agent_pull_request_session_links",
                columns: new[] { "organization_id", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_pull_request_session_links_organization_id_pull_reque",
                schema: "zeeq",
                table: "agent_pull_request_session_links",
                columns: new[] { "organization_id", "pull_request_record_id", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_raw_requests_ingest_organization_id",
                schema: "zeeq",
                table: "telemetry_raw_requests",
                column: "ingest_organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_telemetry_raw_requests_processing_status",
                schema: "zeeq",
                table: "telemetry_raw_requests",
                column: "processing_status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove part_config before dropping the partitioned table.
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
                            'zeeq.agent_session_events'
                        );
                    END IF;
                END $$;
                """
            );

            migrationBuilder.DropTable(
                name: "agent_session_events",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "agent_conversations",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "agent_pull_request_session_links",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "telemetry_raw_requests",
                schema: "zeeq");
        }
    }
}
