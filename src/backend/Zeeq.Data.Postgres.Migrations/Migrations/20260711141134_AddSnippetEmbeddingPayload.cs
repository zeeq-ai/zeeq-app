using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddSnippetEmbeddingPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "embedding_payload",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "embedding_payload",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "embedding_payload",
                schema: "zeeq",
                table: "docs_public_document_snippets");

            migrationBuilder.DropColumn(
                name: "embedding_payload",
                schema: "zeeq",
                table: "docs_library_document_snippets");
        }
    }
}
