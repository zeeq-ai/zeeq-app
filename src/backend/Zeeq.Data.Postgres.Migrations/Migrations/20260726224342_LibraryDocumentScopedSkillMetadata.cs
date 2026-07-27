using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class LibraryDocumentScopedSkillMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "as_scoped_skill",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_organization_id_as_scoped_skill_libr",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "as_scoped_skill", "library_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_organization_id_as_scoped_skill_libr",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "as_scoped_skill",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "metadata",
                schema: "zeeq",
                table: "docs_library_documents");
        }
    }
}
