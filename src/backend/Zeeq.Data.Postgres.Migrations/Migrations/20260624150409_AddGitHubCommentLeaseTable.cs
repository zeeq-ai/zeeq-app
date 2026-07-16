using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubCommentLeaseTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder
                .CreateTable(
                    name: "code_review_github_comment_leases",
                    schema: "zeeq",
                    columns: table => new
                    {
                        lease_key = table.Column<string>(
                            type: "character varying(512)",
                            maxLength: 512,
                            nullable: false
                        ),
                        worker_id = table.Column<string>(
                            type: "character varying(256)",
                            maxLength: 256,
                            nullable: false
                        ),
                        acquired_at_utc = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: false
                        ),
                        expires_at_utc = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: false
                        ),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("pk_code_review_github_comment_leases", x => x.lease_key);
                    }
                )
                .Annotation("Npgsql:UnloggedTable", true);

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_comment_leases_expires_at_utc",
                schema: "zeeq",
                table: "code_review_github_comment_leases",
                column: "expires_at_utc"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "code_review_github_comment_leases", schema: "zeeq");
        }
    }
}
