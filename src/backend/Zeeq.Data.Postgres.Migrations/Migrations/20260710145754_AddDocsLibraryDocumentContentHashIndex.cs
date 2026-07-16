using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddDocsLibraryDocumentContentHashIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_organization_id_library_id_content_h",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "library_id", "content_hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_organization_id_library_id_content_h",
                schema: "zeeq",
                table: "docs_library_documents");
        }
    }
}
