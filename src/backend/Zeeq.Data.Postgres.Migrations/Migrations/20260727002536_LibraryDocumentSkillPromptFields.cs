using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class LibraryDocumentSkillPromptFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "skill_description",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "skill_description_override",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "skill_name",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "skill_name_override",
                schema: "zeeq",
                table: "docs_library_documents",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_scoped_skill_name",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "as_scoped_skill", "skill_name" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_documents_scoped_skill_name_override",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "as_scoped_skill", "skill_name_override" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_scoped_skill_name",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropIndex(
                name: "ix_docs_library_documents_scoped_skill_name_override",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "skill_description",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "skill_description_override",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "skill_name",
                schema: "zeeq",
                table: "docs_library_documents");

            migrationBuilder.DropColumn(
                name: "skill_name_override",
                schema: "zeeq",
                table: "docs_library_documents");
        }
    }
}
