using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubCommentAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_review_github_comment_anchors",
                schema: "zeeq",
                columns: table => new
                {
                    target_key = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
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
                    owner_qualified_repo_name = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    pull_request_number = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    scope_key = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    git_hub_comment_id = table.Column<long>(type: "bigint", nullable: true),
                    last_resolved_at_utc = table.Column<DateTimeOffset>(
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
                    table.PrimaryKey("pk_code_review_github_comment_anchors", x => x.target_key);
                    table.ForeignKey(
                        name: "fk_code_review_github_comment_anchors_code_review_repositories",
                        column: x => x.repository_id,
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_code_review_github_comment_anchors_organizations_organizati",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_comment_anchors_organization_id_reposito",
                schema: "zeeq",
                table: "code_review_github_comment_anchors",
                columns: new[]
                {
                    "organization_id",
                    "repository_id",
                    "pull_request_number",
                    "kind",
                    "scope_key",
                },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_comment_anchors_organization_id_updated_",
                schema: "zeeq",
                table: "code_review_github_comment_anchors",
                columns: new[] { "organization_id", "updated_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_comment_anchors_repository_id_pull_reque",
                schema: "zeeq",
                table: "code_review_github_comment_anchors",
                columns: new[] { "repository_id", "pull_request_number", "kind" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_review_github_comment_anchors",
                schema: "zeeq"
            );
        }
    }
}
