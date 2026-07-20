using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class WidenSnippetMetadataColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropSnippetSearchInfrastructure(migrationBuilder);

            migrationBuilder.AlterColumn<string>(
                name: "tag",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "language",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "heading_path",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "header",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);

            migrationBuilder.AlterColumn<string>(
                name: "tag",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "language",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "heading_path",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);

            migrationBuilder.AlterColumn<string>(
                name: "header",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);

            CreateSnippetSearchInfrastructure(migrationBuilder);

            void DropSnippetSearchInfrastructure(MigrationBuilder builder)
            {
                builder.Sql(
                    """
                    DROP INDEX IF EXISTS zeeq.ix_docs_library_document_snippets_search;
                    DROP INDEX IF EXISTS zeeq.ix_docs_public_document_snippets_search;

                    ALTER TABLE zeeq.docs_library_document_snippets
                        DROP COLUMN search_vector;
                    ALTER TABLE zeeq.docs_public_document_snippets
                        DROP COLUMN search_vector;
                    """
                );
            }

            void CreateSnippetSearchInfrastructure(MigrationBuilder builder)
            {
                builder.Sql(
                    """
                    ALTER TABLE zeeq.docs_library_document_snippets
                        ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
                            setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||
                            setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||
                            setweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||
                            setweight(to_tsvector('english', coalesce(content, '')), 'D')
                        ) STORED;

                    ALTER TABLE zeeq.docs_public_document_snippets
                        ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
                            setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||
                            setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||
                            setweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||
                            setweight(to_tsvector('english', coalesce(content, '')), 'D')
                        ) STORED;

                    -- NOTE: This intentionally mirrors AddDocumentSnippets. The btree_gin
                    -- extension allows the scalar scope columns to participate in this GIN index
                    -- alongside search_vector.
                    CREATE INDEX ix_docs_library_document_snippets_search
                        ON zeeq.docs_library_document_snippets
                        USING gin (organization_id, library_id, kind, search_vector);

                    CREATE INDEX ix_docs_public_document_snippets_search
                        ON zeeq.docs_public_document_snippets
                        USING gin (public_source_id, kind, search_vector);
                    """
                );
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropSnippetSearchInfrastructure(migrationBuilder);
            TruncateSnippetMetadataToOldLimits(migrationBuilder);

            migrationBuilder.AlterColumn<string>(
                name: "tag",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "language",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "heading_path",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "header",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "tag",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "language",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "heading_path",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "header",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            CreateSnippetSearchInfrastructure(migrationBuilder);

            void DropSnippetSearchInfrastructure(MigrationBuilder builder)
            {
                builder.Sql(
                    """
                    DROP INDEX IF EXISTS zeeq.ix_docs_library_document_snippets_search;
                    DROP INDEX IF EXISTS zeeq.ix_docs_public_document_snippets_search;

                    ALTER TABLE zeeq.docs_library_document_snippets
                        DROP COLUMN search_vector;
                    ALTER TABLE zeeq.docs_public_document_snippets
                        DROP COLUMN search_vector;
                    """
                );
            }

            void TruncateSnippetMetadataToOldLimits(MigrationBuilder builder)
            {
                builder.Sql(
                    """
                    UPDATE zeeq.docs_library_document_snippets
                    SET
                        header = left(header, 1024),
                        heading_path = left(heading_path, 2048),
                        language = CASE WHEN language IS NULL THEN NULL ELSE left(language, 64) END,
                        tag = CASE WHEN tag IS NULL THEN NULL ELSE left(tag, 256) END
                    WHERE length(header) > 1024
                       OR length(heading_path) > 2048
                       OR length(language) > 64
                       OR length(tag) > 256;

                    UPDATE zeeq.docs_public_document_snippets
                    SET
                        header = left(header, 1024),
                        heading_path = left(heading_path, 2048),
                        language = CASE WHEN language IS NULL THEN NULL ELSE left(language, 64) END,
                        tag = CASE WHEN tag IS NULL THEN NULL ELSE left(tag, 256) END
                    WHERE length(header) > 1024
                       OR length(heading_path) > 2048
                       OR length(language) > 64
                       OR length(tag) > 256;
                    """
                );
            }

            void CreateSnippetSearchInfrastructure(MigrationBuilder builder)
            {
                builder.Sql(
                    """
                    ALTER TABLE zeeq.docs_library_document_snippets
                        ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
                            setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||
                            setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||
                            setweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||
                            setweight(to_tsvector('english', coalesce(content, '')), 'D')
                        ) STORED;

                    ALTER TABLE zeeq.docs_public_document_snippets
                        ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (
                            setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||
                            setweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||
                            setweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||
                            setweight(to_tsvector('english', coalesce(content, '')), 'D')
                        ) STORED;

                    -- NOTE: This intentionally mirrors AddDocumentSnippets. The btree_gin
                    -- extension allows the scalar scope columns to participate in this GIN index
                    -- alongside search_vector.
                    CREATE INDEX ix_docs_library_document_snippets_search
                        ON zeeq.docs_library_document_snippets
                        USING gin (organization_id, library_id, kind, search_vector);

                    CREATE INDEX ix_docs_public_document_snippets_search
                        ON zeeq.docs_public_document_snippets
                        USING gin (public_source_id, kind, search_vector);
                    """
                );
            }
        }
    }
}
