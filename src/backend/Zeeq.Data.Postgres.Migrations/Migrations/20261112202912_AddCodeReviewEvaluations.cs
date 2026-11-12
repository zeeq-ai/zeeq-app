using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeReviewEvaluations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // GUARDED BASELINE REPAIR (do not remove).
            //
            // The model declares an organization-qualified alternate key on
            // code_review_repositories, and the original migration chain adds it, but at least
            // one deployed baseline is missing it (schema drift). The evaluation tables below
            // use that key for their repository foreign keys, so create it only when no
            // equivalent non-partial unique key or index exists. On a clean chain this is a
            // no-op. Down intentionally does not drop it: it is required by the model and may
            // predate this migration.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    has_key boolean;
                BEGIN
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_index AS i
                        JOIN pg_class AS t ON t.oid = i.indrelid
                        JOIN pg_namespace AS n ON n.oid = t.relnamespace
                        WHERE n.nspname = 'zeeq'
                          AND t.relname = 'code_review_repositories'
                          AND i.indisunique
                          AND i.indpred IS NULL
                          AND i.indexprs IS NULL
                          AND (
                              SELECT array_agg(a.attname::text ORDER BY a.attname)
                              FROM unnest(i.indkey) AS k(attnum)
                              JOIN pg_attribute AS a
                                ON a.attrelid = t.oid AND a.attnum = k.attnum
                          ) = ARRAY['id', 'organization_id']
                    ) INTO has_key;

                    IF NOT has_key THEN
                        ALTER TABLE zeeq.code_review_repositories
                            ADD CONSTRAINT ak_code_review_repositories_organization_id_id
                            UNIQUE (organization_id, id);
                    END IF;
                END $$;
                """
            );

            migrationBuilder.AddUniqueConstraint(
                name: "ak_code_reviewer_agents_organization_id_id",
                schema: "zeeq",
                table: "code_reviewer_agents",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_code_reviewer_agents_organization_id_repository_id_id",
                schema: "zeeq",
                table: "code_reviewer_agents",
                columns: new[] { "organization_id", "repository_id", "id" });

            migrationBuilder.CreateTable(
                name: "code_review_evaluation_datasets",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    repository_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    create_request_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    archived_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_evaluation_datasets", x => new { x.organization_id, x.id });
                    table.UniqueConstraint("ak_code_review_evaluation_datasets_organization_id_repository_", x => new { x.organization_id, x.repository_id, x.id });
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_datasets_code_review_repositories_or",
                        columns: x => new { x.organization_id, x.repository_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_datasets_organizations_organization_",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_datasets_teams_organization_id_team_",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_reviewer_agent_revisions",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    repository_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    agent_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    configuration = table.Column<string>(type: "jsonb", nullable: false),
                    created_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_reviewer_agent_revisions", x => new { x.organization_id, x.id });
                    table.UniqueConstraint("ak_code_reviewer_agent_revisions_organization_id_repository_id", x => new { x.organization_id, x.repository_id, x.id });
                    table.ForeignKey(
                        name: "fk_code_reviewer_agent_revisions_code_review_repositories_orga",
                        columns: x => new { x.organization_id, x.repository_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_reviewer_agent_revisions_code_reviewer_agents_organiza",
                        columns: x => new { x.organization_id, x.repository_id, x.agent_id },
                        principalSchema: "zeeq",
                        principalTable: "code_reviewer_agents",
                        principalColumns: new[] { "organization_id", "repository_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_reviewer_agent_revisions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_review_evaluation_cases",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    repository_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    dataset_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    pull_request_title = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    pull_request_record_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    snapshot_storage_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    snapshot_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_byte_count = table.Column<int>(type: "integer", nullable: false),
                    snapshot_schema_version = table.Column<int>(type: "integer", nullable: false),
                    base_sha = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    head_sha = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    pull_request_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    captured_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_file_count = table.Column<int>(type: "integer", nullable: false),
                    omitted_file_count = table.Column<int>(type: "integer", nullable: false),
                    declared_changed_file_count = table.Column<int>(type: "integer", nullable: false),
                    removed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reference_sets = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_evaluation_cases", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_cases_code_review_evaluation_dataset",
                        columns: x => new { x.organization_id, x.repository_id, x.dataset_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_evaluation_datasets",
                        principalColumns: new[] { "organization_id", "repository_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_cases_code_review_repositories_organ",
                        columns: x => new { x.organization_id, x.repository_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_cases_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_review_evaluation_runs",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    repository_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    dataset_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    dataset_version = table.Column<long>(type: "bigint", nullable: false),
                    agent_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    saved_revision_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                    requested_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parent_run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    request_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    lease_token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    case_count = table.Column<int>(type: "integer", nullable: false),
                    graded_case_count = table.Column<int>(type: "integer", nullable: false),
                    skipped_case_count = table.Column<int>(type: "integer", nullable: false),
                    failed_case_count = table.Column<int>(type: "integer", nullable: false),
                    unlabelled_case_count = table.Column<int>(type: "integer", nullable: false),
                    matched_reference_count = table.Column<int>(type: "integer", nullable: false),
                    missed_reference_count = table.Column<int>(type: "integer", nullable: false),
                    expected_reference_count = table.Column<int>(type: "integer", nullable: false),
                    additional_finding_count = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("ak_code_review_evaluation_runs_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_runs_code_review_evaluation_datasets",
                        columns: x => new { x.organization_id, x.repository_id, x.dataset_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_evaluation_datasets",
                        principalColumns: new[] { "organization_id", "repository_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_runs_code_review_evaluation_runs_org",
                        columns: x => new { x.organization_id, x.parent_run_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_evaluation_runs",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_runs_code_review_repositories_organi",
                        columns: x => new { x.organization_id, x.repository_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_runs_code_reviewer_agent_revisions_o",
                        columns: x => new { x.organization_id, x.repository_id, x.saved_revision_id },
                        principalSchema: "zeeq",
                        principalTable: "code_reviewer_agent_revisions",
                        principalColumns: new[] { "organization_id", "repository_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_runs_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_review_evaluation_case_results",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    manifest_case_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    candidate_storage_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    candidate_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    candidate_byte_count = table.Column<int>(type: "integer", nullable: true),
                    candidate_finding_count = table.Column<int>(type: "integer", nullable: false),
                    matched_reference_count = table.Column<int>(type: "integer", nullable: false),
                    missed_reference_count = table.Column<int>(type: "integer", nullable: false),
                    expected_reference_count = table.Column<int>(type: "integer", nullable: false),
                    additional_finding_count = table.Column<int>(type: "integer", nullable: false),
                    candidate_findings_json = table.Column<string>(type: "jsonb", nullable: true),
                    judge_matches_json = table.Column<string>(type: "jsonb", nullable: true),
                    judge_reasons_json = table.Column<string>(type: "jsonb", nullable: true),
                    reviewer_provenance_json = table.Column<string>(type: "jsonb", nullable: true),
                    judge_provenance_json = table.Column<string>(type: "jsonb", nullable: true),
                    reviewer_input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    reviewer_output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    judge_input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    judge_output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    error_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    failure_message = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_evaluation_case_results", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_case_results_code_review_evaluation_",
                        columns: x => new { x.organization_id, x.run_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_evaluation_runs",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_code_review_evaluation_case_results_organizations_organizat",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_case_results_organization_id_run_id_",
                schema: "zeeq",
                table: "code_review_evaluation_case_results",
                columns: new[] { "organization_id", "run_id", "manifest_case_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_case_results_organization_id_run_id_1",
                schema: "zeeq",
                table: "code_review_evaluation_case_results",
                columns: new[] { "organization_id", "run_id", "phase", "manifest_case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_cases_organization_id_dataset_id_pul",
                schema: "zeeq",
                table: "code_review_evaluation_cases",
                columns: new[] { "organization_id", "dataset_id", "pull_request_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_cases_organization_id_dataset_id_rem",
                schema: "zeeq",
                table: "code_review_evaluation_cases",
                columns: new[] { "organization_id", "dataset_id", "removed_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_cases_organization_id_repository_id_",
                schema: "zeeq",
                table: "code_review_evaluation_cases",
                columns: new[] { "organization_id", "repository_id", "dataset_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_datasets_organization_id_create_requ",
                schema: "zeeq",
                table: "code_review_evaluation_datasets",
                columns: new[] { "organization_id", "create_request_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_datasets_organization_id_repository_",
                schema: "zeeq",
                table: "code_review_evaluation_datasets",
                columns: new[] { "organization_id", "repository_id", "archived_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_datasets_organization_id_team_id",
                schema: "zeeq",
                table: "code_review_evaluation_datasets",
                columns: new[] { "organization_id", "team_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_runs_organization_id_parent_run_id",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                columns: new[] { "organization_id", "parent_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_runs_organization_id_repository_id_d",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                columns: new[] { "organization_id", "repository_id", "dataset_id", "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_runs_organization_id_repository_id_s",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                columns: new[] { "organization_id", "repository_id", "saved_revision_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_review_evaluation_runs_organization_id_request_key",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                columns: new[] { "organization_id", "request_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_code_review_evaluation_runs_active_per_org",
                schema: "zeeq",
                table: "code_review_evaluation_runs",
                column: "organization_id",
                unique: true,
                filter: "state IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "ix_code_reviewer_agent_revisions_organization_id_agent_id_crea",
                schema: "zeeq",
                table: "code_reviewer_agent_revisions",
                columns: new[] { "organization_id", "agent_id", "created_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_reviewer_agent_revisions_organization_id_repository_id",
                schema: "zeeq",
                table: "code_reviewer_agent_revisions",
                columns: new[] { "organization_id", "repository_id", "agent_id", "content_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_review_evaluation_case_results",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "code_review_evaluation_cases",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "code_review_evaluation_runs",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "code_review_evaluation_datasets",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "code_reviewer_agent_revisions",
                schema: "zeeq");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_code_reviewer_agents_organization_id_id",
                schema: "zeeq",
                table: "code_reviewer_agents");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_code_reviewer_agents_organization_id_repository_id_id",
                schema: "zeeq",
                table: "code_reviewer_agents");
        }
    }
}
