using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class LibraryDocumentPreviousPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "previous_paths",
                schema: "zeeq",
                table: "library_document",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateIndex(
                name: "ix_library_document_previous_paths",
                schema: "zeeq",
                table: "library_document",
                column: "previous_paths")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_library_document_previous_paths",
                schema: "zeeq",
                table: "library_document");

            migrationBuilder.DropColumn(
                name: "previous_paths",
                schema: "zeeq",
                table: "library_document");
        }
    }
}
