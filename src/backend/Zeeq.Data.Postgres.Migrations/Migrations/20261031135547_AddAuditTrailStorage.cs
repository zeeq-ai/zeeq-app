using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditTrailStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF cannot express PARTITION BY, so both tables are declared as range-partitioned
            // by occurred_at_utc. The partition key is part of each primary key. pg_partman owns
            // monthly UTC child creation; retention is deliberately NULL and tables are kept,
            // so audit history is never detached or dropped automatically. The EF-generated
            // indexes below are created on the partitioned parents and propagate to children.
            migrationBuilder.Sql(
                """
                CREATE TABLE zeeq.organization_audit_events (
                    id uuid NOT NULL,
                    occurred_at_utc timestamp with time zone NOT NULL,
                    organization_id character varying(128) NOT NULL,
                    team_id character varying(128),
                    organization_label character varying(512),
                    surface_in_system boolean NOT NULL,
                    event_type character varying(128) NOT NULL,
                    change_type character varying(32) NOT NULL,
                    area character varying(64) NOT NULL,
                    subject_type character varying(64) NOT NULL,
                    subject_id character varying(256) NOT NULL,
                    subject_label character varying(512) NOT NULL,
                    library_id character varying(128),
                    actor_kind character varying(64) NOT NULL,
                    actor_id character varying(256) NOT NULL,
                    actor_display_name character varying(512) NOT NULL,
                    actor_channel character varying(64) NOT NULL,
                    actor_responsible_user_id character varying(256),
                    actor_credential_id character varying(256),
                    actor_credential_name character varying(512),
                    actor_initiated_by_user_id character varying(256),
                    schema_version integer NOT NULL DEFAULT 1,
                    operation_id uuid NOT NULL,
                    request_id character varying(256),
                    changed_fields text[] NOT NULL,
                    source_reference character varying(512),
                    before_hash character varying(128),
                    after_hash character varying(128),
                    outcome character varying(64),
                    before_snapshot jsonb,
                    after_snapshot jsonb,
                    CONSTRAINT pk_organization_audit_events PRIMARY KEY (organization_id, occurred_at_utc, id)
                ) PARTITION BY RANGE (occurred_at_utc);

                CREATE TABLE zeeq.system_audit_events (
                    id uuid NOT NULL,
                    occurred_at_utc timestamp with time zone NOT NULL,
                    organization_label character varying(512),
                    event_type character varying(128) NOT NULL,
                    change_type character varying(32) NOT NULL,
                    area character varying(64) NOT NULL,
                    subject_type character varying(64) NOT NULL,
                    subject_id character varying(256) NOT NULL,
                    subject_label character varying(512) NOT NULL,
                    library_id character varying(128),
                    actor_kind character varying(64) NOT NULL,
                    actor_id character varying(256) NOT NULL,
                    actor_display_name character varying(512) NOT NULL,
                    actor_channel character varying(64) NOT NULL,
                    actor_responsible_user_id character varying(256),
                    actor_credential_id character varying(256),
                    actor_credential_name character varying(512),
                    actor_initiated_by_user_id character varying(256),
                    schema_version integer NOT NULL DEFAULT 1,
                    operation_id uuid NOT NULL,
                    request_id character varying(256),
                    changed_fields text[] NOT NULL,
                    source_reference character varying(512),
                    before_hash character varying(128),
                    after_hash character varying(128),
                    outcome character varying(64),
                    before_snapshot jsonb,
                    after_snapshot jsonb,
                    CONSTRAINT pk_system_audit_events PRIMARY KEY (occurred_at_utc, id)
                ) PARTITION BY RANGE (occurred_at_utc);
                """
            );

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    partman_schema name;
                    parent_name text;
                    previous_timezone text := current_setting('timezone');
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

                    -- Partition bounds on timestamptz follow the session time zone; pin UTC so
                    -- monthly boundaries are UTC midnight on the first of the month.
                    PERFORM set_config('timezone', 'UTC', false);

                    FOREACH parent_name IN ARRAY ARRAY['zeeq.organization_audit_events', 'zeeq.system_audit_events']
                    LOOP
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
                            parent_name,
                            'occurred_at_utc',
                            '1 month',
                            'range',
                            'none',
                            3,
                            (date_trunc('month', now() AT TIME ZONE 'UTC') - interval '1 month')::text
                        );

                        -- Retention is NULL: partitions are never detached or dropped. Infinite
                        -- time partitions keep premaking future months even when idle.
                        EXECUTE format(
                            'UPDATE %I.part_config
                             SET infinite_time_partitions = true,
                                 retention = NULL,
                                 retention_keep_table = true,
                                 retention_keep_index = true
                             WHERE parent_table = %L',
                            partman_schema,
                            parent_name
                        );
                    END LOOP;

                    PERFORM set_config('timezone', previous_timezone, false);
                END $$;
                """
            );

            migrationBuilder.CreateIndex(
                name: "ix_organization_audit_events_organization_id_library_id_occurr",
                schema: "zeeq",
                table: "organization_audit_events",
                columns: new[] { "organization_id", "library_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_organization_audit_events_organization_id_operation_id_occu",
                schema: "zeeq",
                table: "organization_audit_events",
                columns: new[] { "organization_id", "operation_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_organization_audit_events_organization_id_subject_type_subj",
                schema: "zeeq",
                table: "organization_audit_events",
                columns: new[] { "organization_id", "subject_type", "subject_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_organization_audit_events_system_surface",
                schema: "zeeq",
                table: "organization_audit_events",
                columns: new[] { "occurred_at_utc", "id" },
                filter: "surface_in_system");

            migrationBuilder.CreateIndex(
                name: "ix_system_audit_events_operation_id_occurred_at_utc",
                schema: "zeeq",
                table: "system_audit_events",
                columns: new[] { "operation_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_system_audit_events_subject_type_subject_id_occurred_at_utc",
                schema: "zeeq",
                table: "system_audit_events",
                columns: new[] { "subject_type", "subject_id", "occurred_at_utc", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove pg_partman configuration first so maintenance stops managing dropped tables.
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
                            'DELETE FROM %I.part_config WHERE parent_table IN (%L, %L)',
                            partman_schema,
                            'zeeq.organization_audit_events',
                            'zeeq.system_audit_events'
                        );
                    END IF;
                END $$;
                """
            );

            migrationBuilder.DropTable(
                name: "organization_audit_events",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "system_audit_events",
                schema: "zeeq");
        }
    }
}
