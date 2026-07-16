using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class DropGitHubCommentDesiredStateTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_review_github_comment_desired_states",
                schema: "zeeq"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
                    command_kind = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    command_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    git_hub_issue_comment_id = table.Column<long>(type: "bigint", nullable: true),
                    owner_qualified_repo_name = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    trace_context_json = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    version = table.Column<long>(type: "bigint", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_comment_desired_states_organization_id_u",
                schema: "zeeq",
                table: "code_review_github_comment_desired_states",
                columns: new[] { "organization_id", "updated_at_utc" }
            );
        }
    }
}
