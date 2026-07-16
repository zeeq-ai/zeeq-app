using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class CodeRepositoryLibraryIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "library_ids",
                schema: "zeeq",
                table: "code_review_repositories",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateIndex(
                name: "ix_code_repository_library_ids",
                schema: "zeeq",
                table: "code_review_repositories",
                column: "library_ids")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_code_repository_library_ids",
                schema: "zeeq",
                table: "code_review_repositories");

            migrationBuilder.DropColumn(
                name: "library_ids",
                schema: "zeeq",
                table: "code_review_repositories");
        }
    }
}
