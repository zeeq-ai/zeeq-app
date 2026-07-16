using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubCodeReviewStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_review_active_locks",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    pull_request_record_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    pull_request_created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    code_review_record_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    code_review_created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    status = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    acquired_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_code_review_active_locks",
                        x => new { x.organization_id, x.pull_request_record_id }
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "code_review_github_comment_desired_states",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    owner_qualified_repo_name = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    command_kind = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    command_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    trace_context_json = table.Column<string>(type: "jsonb", nullable: false),
                    git_hub_issue_comment_id = table.Column<long>(type: "bigint", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_code_review_github_comment_desired_states",
                        x => new
                        {
                            x.organization_id,
                            x.repository_id,
                            x.pull_request_number,
                        }
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "code_review_github_webhook_deliveries",
                schema: "zeeq",
                columns: table => new
                {
                    delivery_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    event_name = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    action = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    received_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    processed_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    status = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    failure_message = table.Column<string>(
                        type: "character varying(4096)",
                        maxLength: 4096,
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_code_review_github_webhook_deliveries",
                        x => x.delivery_id
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "code_review_pull_request_lookups",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    owner_qualified_repo_name = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    pull_request_record_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    pull_request_created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_code_review_pull_request_lookups",
                        x => new
                        {
                            x.organization_id,
                            x.repository_id,
                            x.pull_request_number,
                        }
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "code_review_records",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    pull_request_record_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    owner_qualified_repo_name = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    branch = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    title = table.Column<string>(
                        type: "character varying(1024)",
                        maxLength: 1024,
                        nullable: false
                    ),
                    author_login = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    status = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    request_origin = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    review_group_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    remaining_review_budget = table.Column<int>(type: "integer", nullable: false),
                    critical_findings = table.Column<int>(type: "integer", nullable: false),
                    major_findings = table.Column<int>(type: "integer", nullable: false),
                    minor_findings = table.Column<int>(type: "integer", nullable: false),
                    suggestion_findings = table.Column<int>(type: "integer", nullable: false),
                    comment_findings = table.Column<int>(type: "integer", nullable: false),
                    findings_payload = table.Column<string>(type: "jsonb", nullable: false),
                    findings_storage_uri = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: true
                    ),
                    failure_message = table.Column<string>(
                        type: "character varying(4096)",
                        maxLength: 4096,
                        nullable: true
                    ),
                    disabled_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_records", x => new { x.id, x.created_at_utc });
                }
            );

            migrationBuilder.DropTable(name: "code_review_records", schema: "zeeq");

            migrationBuilder.Sql(
                """
                CREATE TABLE zeeq.code_review_records (
                    id character varying(128) NOT NULL,
                    created_at_utc timestamp with time zone NOT NULL,
                    organization_id character varying(128) NOT NULL,
                    team_id character varying(128),
                    pull_request_record_id character varying(128) NOT NULL,
                    repository_id character varying(128) NOT NULL,
                    owner_qualified_repo_name character varying(512) NOT NULL,
                    pull_request_number integer NOT NULL,
                    branch character varying(512) NOT NULL,
                    title character varying(1024) NOT NULL,
                    author_login character varying(256) NOT NULL,
                    status character varying(32) NOT NULL,
                    request_origin character varying(32) NOT NULL,
                    review_group_id character varying(128),
                    remaining_review_budget integer NOT NULL,
                    critical_findings integer NOT NULL,
                    major_findings integer NOT NULL,
                    minor_findings integer NOT NULL,
                    suggestion_findings integer NOT NULL,
                    comment_findings integer NOT NULL,
                    findings_payload jsonb NOT NULL,
                    findings_storage_uri character varying(2048),
                    failure_message character varying(4096),
                    disabled_at_utc timestamp with time zone,
                    updated_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT pk_code_review_records PRIMARY KEY (id, created_at_utc)
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
                        'zeeq.code_review_records',
                        'created_at_utc',
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
                        'zeeq.code_review_records'
                    );
                END $$;
                """
            );

            migrationBuilder.CreateTable(
                name: "code_review_repositories",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    provider = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    owner_qualified_name = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    display_name = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    configuration_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    disabled_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_repositories", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_review_repositories_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_code_review_repositories_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "code_review_pull_request_records",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    owner_qualified_repo_name = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    git_hub_node_id = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    title = table.Column<string>(
                        type: "character varying(1024)",
                        maxLength: 1024,
                        nullable: false
                    ),
                    branch = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    base_branch = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    head_sha = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    author_login = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    html_url = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: false
                    ),
                    is_draft = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    claim_status = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    claimed_by_user_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    feature_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    tags_json = table.Column<string>(type: "jsonb", nullable: false),
                    labels_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_from_webhook_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    last_webhook_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    disabled_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_code_review_pull_request_records",
                        x => new { x.id, x.created_at_utc }
                    );
                    table.ForeignKey(
                        name: "fk_code_review_pull_request_records_code_review_repositories_r",
                        column: x => x.repository_id,
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_code_review_pull_request_records_core_organizations_organiz",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.DropTable(name: "code_review_pull_request_records", schema: "zeeq");

            migrationBuilder.Sql(
                """
                CREATE TABLE zeeq.code_review_pull_request_records (
                    id character varying(128) NOT NULL,
                    created_at_utc timestamp with time zone NOT NULL,
                    organization_id character varying(128) NOT NULL,
                    team_id character varying(128),
                    repository_id character varying(128) NOT NULL,
                    owner_qualified_repo_name character varying(512) NOT NULL,
                    pull_request_number integer NOT NULL,
                    git_hub_node_id character varying(256) NOT NULL,
                    title character varying(1024) NOT NULL,
                    branch character varying(512) NOT NULL,
                    base_branch character varying(512) NOT NULL,
                    head_sha character varying(128) NOT NULL,
                    author_login character varying(256) NOT NULL,
                    html_url character varying(2048) NOT NULL,
                    is_draft boolean NOT NULL,
                    state character varying(32) NOT NULL,
                    claim_status character varying(32) NOT NULL,
                    claimed_by_user_id character varying(128),
                    feature_id character varying(128),
                    tags_json jsonb NOT NULL,
                    labels_json jsonb NOT NULL,
                    created_from_webhook_at_utc timestamp with time zone NOT NULL,
                    last_webhook_at_utc timestamp with time zone NOT NULL,
                    disabled_at_utc timestamp with time zone,
                    updated_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT pk_code_review_pull_request_records PRIMARY KEY (id, created_at_utc),
                    CONSTRAINT fk_code_review_pull_request_records_code_review_repositories_r
                        FOREIGN KEY (repository_id)
                        REFERENCES zeeq.code_review_repositories (id)
                        ON DELETE RESTRICT,
                    CONSTRAINT fk_code_review_pull_request_records_core_organizations_organiz
                        FOREIGN KEY (organization_id)
                        REFERENCES zeeq.core_organizations (id)
                        ON DELETE RESTRICT
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
                        'zeeq.code_review_pull_request_records',
                        'created_at_utc',
                        '30 days',
                        'range',
                        'none',
                        4,
                        (date_trunc('day', now()) - interval '30 days')::text
                    );

                    EXECUTE format(
                        'UPDATE %I.part_config
                         SET infinite_time_partitions = true
                         WHERE parent_table = %L',
                        partman_schema,
                        'zeeq.code_review_pull_request_records'
                    );
                END $$;
                """
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_active_locks_code_review_record_id",
                schema: "zeeq",
                table: "code_review_active_locks",
                column: "code_review_record_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_active_locks_organization_id_team_id_updated_at",
                schema: "zeeq",
                table: "code_review_active_locks",
                columns: new[] { "organization_id", "team_id", "updated_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_comment_desired_states_organization_id_u",
                schema: "zeeq",
                table: "code_review_github_comment_desired_states",
                columns: new[] { "organization_id", "updated_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_webhook_deliveries_organization_id_recei",
                schema: "zeeq",
                table: "code_review_github_webhook_deliveries",
                columns: new[] { "organization_id", "received_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_pull_request_lookups_organization_id_team_id_up",
                schema: "zeeq",
                table: "code_review_pull_request_lookups",
                columns: new[] { "organization_id", "team_id", "updated_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_pull_request_lookups_pull_request_record_id",
                schema: "zeeq",
                table: "code_review_pull_request_lookups",
                column: "pull_request_record_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_pull_request_records_organization_id_claim_stat",
                schema: "zeeq",
                table: "code_review_pull_request_records",
                columns: new[] { "organization_id", "claim_status", "created_at_utc", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_pull_request_records_organization_id_team_id_cr",
                schema: "zeeq",
                table: "code_review_pull_request_records",
                columns: new[] { "organization_id", "team_id", "created_at_utc", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_pull_request_records_repository_id_pull_request",
                schema: "zeeq",
                table: "code_review_pull_request_records",
                columns: new[] { "repository_id", "pull_request_number", "created_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_records_organization_id_request_origin_created_",
                schema: "zeeq",
                table: "code_review_records",
                columns: new[] { "organization_id", "request_origin", "created_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_records_organization_id_team_id_created_at_utc",
                schema: "zeeq",
                table: "code_review_records",
                columns: new[] { "organization_id", "team_id", "created_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_records_pull_request_record_id_status",
                schema: "zeeq",
                table: "code_review_records",
                columns: new[] { "pull_request_record_id", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_repositories_organization_id_provider_owner_qua",
                schema: "zeeq",
                table: "code_review_repositories",
                columns: new[] { "organization_id", "provider", "owner_qualified_name" },
                unique: true,
                filter: "disabled_at_utc IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_repositories_organization_id_team_id_disabled_a",
                schema: "zeeq",
                table: "code_review_repositories",
                columns: new[] { "organization_id", "team_id", "disabled_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_repositories_provider_owner_qualified_name_disa",
                schema: "zeeq",
                table: "code_review_repositories",
                columns: new[] { "provider", "owner_qualified_name", "disabled_at_utc" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                            'DELETE FROM %I.part_config
                             WHERE parent_table IN (%L, %L)',
                            partman_schema,
                            'zeeq.code_review_pull_request_records',
                            'zeeq.code_review_records'
                        );
                    END IF;
                END $$;
                """
            );

            migrationBuilder.DropTable(name: "code_review_active_locks", schema: "zeeq");

            migrationBuilder.DropTable(
                name: "code_review_github_comment_desired_states",
                schema: "zeeq"
            );

            migrationBuilder.DropTable(
                name: "code_review_github_webhook_deliveries",
                schema: "zeeq"
            );

            migrationBuilder.DropTable(name: "code_review_pull_request_lookups", schema: "zeeq");

            migrationBuilder.DropTable(name: "code_review_pull_request_records", schema: "zeeq");

            migrationBuilder.DropTable(name: "code_review_records", schema: "zeeq");

            migrationBuilder.DropTable(name: "code_review_repositories", schema: "zeeq");
        }
    }
}
