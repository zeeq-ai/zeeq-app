using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeReviewFindingTriages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_review_finding_triages",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    repository_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    owner_qualified_repo_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    pull_request_number = table.Column<int>(type: "integer", nullable: true),
                    review_group_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source_review_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_review_created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurrence_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    semantic_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    facet = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: true),
                    finding_summary = table.Column<string>(type: "text", nullable: false),
                    disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    approval_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    current_reason = table.Column<string>(type: "text", nullable: true),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    agent_session_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    last_source_event_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    proposed_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    proposed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleared_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cleared_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    embedding = table.Column<HalfVector>(type: "halfvec(768)", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    embedding_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_review_finding_triages", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_cr_triages_approval_state", "approval_state IN ('Open', 'Proposed', 'Confirmed')");
                    table.CheckConstraint("ck_cr_triages_channel", "channel IN ('Ui', 'GitHub', 'Mcp')");
                    table.CheckConstraint("ck_cr_triages_confirmed_timestamp", "(approval_state = 'Confirmed') = (confirmed_at_utc IS NOT NULL)");
                    table.CheckConstraint("ck_cr_triages_disposition", "disposition IS NULL OR disposition IN ('FalsePositive', 'Deferred')");
                    table.CheckConstraint("ck_cr_triages_disposition_state", "(disposition IS NULL) = (approval_state = 'Open')");
                    table.CheckConstraint("ck_cr_triages_line_positive", "line_number IS NULL OR line_number > 0");
                    table.CheckConstraint("ck_cr_triages_pr_positive", "pull_request_number IS NULL OR pull_request_number > 0");
                    table.CheckConstraint("ck_cr_triages_reason_length", "current_reason IS NULL OR char_length(current_reason) <= 1000");
                    table.CheckConstraint("ck_cr_triages_severity", "severity IN ('Critical', 'Major', 'Minor', 'Suggestion', 'Comment')");
                    table.ForeignKey(
                        name: "fk_cr_triages_org",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cr_triages_repo",
                        columns: x => new { x.organization_id, x.repository_id },
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("ALTER TABLE zeeq.code_review_finding_triages ALTER COLUMN embedding SET STORAGE EXTERNAL;");

            migrationBuilder.CreateIndex(
                name: "ix_cr_triages_embed_queue",
                schema: "zeeq",
                table: "code_review_finding_triages",
                columns: new[] { "updated_at_utc", "organization_id", "id" },
                filter: "disposition = 'FalsePositive' AND approval_state = 'Confirmed'");

            migrationBuilder.CreateIndex(
                name: "ix_cr_triages_fp_by_repo_id",
                schema: "zeeq",
                table: "code_review_finding_triages",
                columns: new[] { "organization_id", "repository_id" },
                filter: "repository_id IS NOT NULL AND disposition = 'FalsePositive' AND approval_state = 'Confirmed'");

            migrationBuilder.CreateIndex(
                name: "ix_cr_triages_fp_by_repo_name",
                schema: "zeeq",
                table: "code_review_finding_triages",
                columns: new[] { "organization_id", "owner_qualified_repo_name" },
                filter: "disposition = 'FalsePositive' AND approval_state = 'Confirmed'");

            migrationBuilder.CreateIndex(
                name: "ix_cr_triages_group_scope",
                schema: "zeeq",
                table: "code_review_finding_triages",
                columns: new[] { "organization_id", "review_group_id" },
                filter: "review_group_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cr_triages_pr_scope",
                schema: "zeeq",
                table: "code_review_finding_triages",
                columns: new[] { "organization_id", "repository_id", "pull_request_number" },
                filter: "pull_request_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_cr_triages_review_occurrence",
                schema: "zeeq",
                table: "code_review_finding_triages",
                columns: new[] { "organization_id", "source_review_id", "occurrence_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_review_finding_triages",
                schema: "zeeq");
        }
    }
}
