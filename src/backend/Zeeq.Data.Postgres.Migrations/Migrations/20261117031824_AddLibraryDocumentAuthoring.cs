using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryDocumentAuthoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "authoring",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "text",
                nullable: false,
                defaultValue: "Manual");
            migrationBuilder.Sql("""
                UPDATE zeeq.docs_library_documents
                SET authoring = 'Synced'
                WHERE sync_run_id IS NOT NULL OR source_external_id IS NOT NULL
                   OR (source_origin IS NOT NULL AND source_origin <> 'null'::jsonb);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "authoring",
                schema: "zeeq",
                table: "docs_library_documents");
        }
    }
}
