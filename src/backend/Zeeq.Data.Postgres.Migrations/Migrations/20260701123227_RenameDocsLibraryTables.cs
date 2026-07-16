using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RenameDocsLibraryTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_libraries_organizations_organization_id",
                schema: "zeeq",
                table: "libraries"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_libraries_teams_organization_id_team_id",
                schema: "zeeq",
                table: "libraries"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_library_document_libraries_organization_id_library_id",
                schema: "zeeq",
                table: "library_document"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_library_document_teams_organization_id_team_id",
                schema: "zeeq",
                table: "library_document"
            );

            migrationBuilder.DropPrimaryKey(
                name: "pk_library_document",
                schema: "zeeq",
                table: "library_document"
            );

            migrationBuilder.DropPrimaryKey(
                name: "pk_libraries",
                schema: "zeeq",
                table: "libraries"
            );

            migrationBuilder.RenameTable(
                name: "library_document",
                schema: "zeeq",
                newName: "docs_library_documents",
                newSchema: "zeeq"
            );

            migrationBuilder.RenameTable(
                name: "libraries",
                schema: "zeeq",
                newName: "docs_libraries",
                newSchema: "zeeq"
            );

            migrationBuilder.RenameIndex(
                name: "ix_library_document_previous_paths",
                schema: "zeeq",
                table: "docs_library_documents",
                newName: "ix_docs_library_documents_previous_paths"
            );

            migrationBuilder.RenameIndex(
                name: "ix_library_document_path",
                schema: "zeeq",
                table: "docs_library_documents",
                newName: "ix_docs_library_documents_path"
            );

            migrationBuilder.RenameIndex(
                name: "ix_library_document_organization_id_team_id",
                schema: "zeeq",
                table: "docs_library_documents",
                newName: "ix_docs_library_documents_organization_id_team_id"
            );

            migrationBuilder.RenameIndex(
                name: "ix_libraries_organization_id_team_id",
                schema: "zeeq",
                table: "docs_libraries",
                newName: "ix_docs_libraries_organization_id_team_id"
            );

            migrationBuilder.RenameIndex(
                name: "ix_libraries_organization_id_name",
                schema: "zeeq",
                table: "docs_libraries",
                newName: "ix_docs_libraries_organization_id_name"
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_docs_library_documents",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "library_id", "id" }
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_docs_libraries",
                schema: "zeeq",
                table: "docs_libraries",
                columns: new[] { "organization_id", "id" }
            );

            migrationBuilder.AddForeignKey(
                name: "fk_docs_libraries_organizations_organization_id",
                schema: "zeeq",
                table: "docs_libraries",
                column: "organization_id",
                principalSchema: "zeeq",
                principalTable: "core_organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_docs_libraries_teams_organization_id_team_id",
                schema: "zeeq",
                table: "docs_libraries",
                columns: new[] { "organization_id", "team_id" },
                principalSchema: "zeeq",
                principalTable: "core_teams",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_docs_library_documents_docs_libraries_organization_id_libra",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "library_id" },
                principalSchema: "zeeq",
                principalTable: "docs_libraries",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "fk_docs_library_documents_teams_organization_id_team_id",
                schema: "zeeq",
                table: "docs_library_documents",
                columns: new[] { "organization_id", "team_id" },
                principalSchema: "zeeq",
                principalTable: "core_teams",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict
            );

            // These indexes are created as raw SQL in the original migration (not modeled
            // via the fluent API), so the migration diff above cannot see or rename them.
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_library_document_search_vector RENAME TO ix_docs_library_documents_search_vector;"
            );
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_library_document_path_reversed RENAME TO ix_docs_library_documents_path_reversed;"
            );
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_library_document_title_trgm RENAME TO ix_docs_library_documents_title_trgm;"
            );
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_library_document_processing_status RENAME TO ix_docs_library_documents_processing_status;"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_docs_library_documents_search_vector RENAME TO ix_library_document_search_vector;"
            );
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_docs_library_documents_path_reversed RENAME TO ix_library_document_path_reversed;"
            );
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_docs_library_documents_title_trgm RENAME TO ix_library_document_title_trgm;"
            );
            migrationBuilder.Sql(
                "ALTER INDEX zeeq.ix_docs_library_documents_processing_status RENAME TO ix_library_document_processing_status;"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_docs_libraries_organizations_organization_id",
                schema: "zeeq",
                table: "docs_libraries"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_docs_libraries_teams_organization_id_team_id",
                schema: "zeeq",
                table: "docs_libraries"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_docs_library_documents_docs_libraries_organization_id_libra",
                schema: "zeeq",
                table: "docs_library_documents"
            );

            migrationBuilder.DropForeignKey(
                name: "fk_docs_library_documents_teams_organization_id_team_id",
                schema: "zeeq",
                table: "docs_library_documents"
            );

            migrationBuilder.DropPrimaryKey(
                name: "pk_docs_library_documents",
                schema: "zeeq",
                table: "docs_library_documents"
            );

            migrationBuilder.DropPrimaryKey(
                name: "pk_docs_libraries",
                schema: "zeeq",
                table: "docs_libraries"
            );

            migrationBuilder.RenameTable(
                name: "docs_library_documents",
                schema: "zeeq",
                newName: "library_document",
                newSchema: "zeeq"
            );

            migrationBuilder.RenameTable(
                name: "docs_libraries",
                schema: "zeeq",
                newName: "libraries",
                newSchema: "zeeq"
            );

            migrationBuilder.RenameIndex(
                name: "ix_docs_library_documents_previous_paths",
                schema: "zeeq",
                table: "library_document",
                newName: "ix_library_document_previous_paths"
            );

            migrationBuilder.RenameIndex(
                name: "ix_docs_library_documents_path",
                schema: "zeeq",
                table: "library_document",
                newName: "ix_library_document_path"
            );

            migrationBuilder.RenameIndex(
                name: "ix_docs_library_documents_organization_id_team_id",
                schema: "zeeq",
                table: "library_document",
                newName: "ix_library_document_organization_id_team_id"
            );

            migrationBuilder.RenameIndex(
                name: "ix_docs_libraries_organization_id_team_id",
                schema: "zeeq",
                table: "libraries",
                newName: "ix_libraries_organization_id_team_id"
            );

            migrationBuilder.RenameIndex(
                name: "ix_docs_libraries_organization_id_name",
                schema: "zeeq",
                table: "libraries",
                newName: "ix_libraries_organization_id_name"
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_library_document",
                schema: "zeeq",
                table: "library_document",
                columns: new[] { "organization_id", "library_id", "id" }
            );

            migrationBuilder.AddPrimaryKey(
                name: "pk_libraries",
                schema: "zeeq",
                table: "libraries",
                columns: new[] { "organization_id", "id" }
            );

            migrationBuilder.AddForeignKey(
                name: "fk_libraries_organizations_organization_id",
                schema: "zeeq",
                table: "libraries",
                column: "organization_id",
                principalSchema: "zeeq",
                principalTable: "core_organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_libraries_teams_organization_id_team_id",
                schema: "zeeq",
                table: "libraries",
                columns: new[] { "organization_id", "team_id" },
                principalSchema: "zeeq",
                principalTable: "core_teams",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "fk_library_document_libraries_organization_id_library_id",
                schema: "zeeq",
                table: "library_document",
                columns: new[] { "organization_id", "library_id" },
                principalSchema: "zeeq",
                principalTable: "libraries",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Cascade
            );

            migrationBuilder.AddForeignKey(
                name: "fk_library_document_teams_organization_id_team_id",
                schema: "zeeq",
                table: "library_document",
                columns: new[] { "organization_id", "team_id" },
                principalSchema: "zeeq",
                principalTable: "core_teams",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict
            );
        }
    }
}
