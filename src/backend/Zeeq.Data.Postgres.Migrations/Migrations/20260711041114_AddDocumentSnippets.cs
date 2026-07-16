using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;
using Pgvector;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSnippets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "docs_library_document_snippets",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    library_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    document_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    header = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    heading_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    language = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    preceding_text = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    identifiers = table.Column<string[]>(type: "text[]", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "halfvec(768)", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    embedding_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||\nsetweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||\nsetweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(content, '')), 'D')", stored: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_docs_library_document_snippets", x => new { x.organization_id, x.library_id, x.id });
                    table.ForeignKey(
                        name: "fk_docs_library_document_snippets_docs_library_documents_organ",
                        columns: x => new { x.organization_id, x.library_id, x.document_id },
                        principalSchema: "zeeq",
                        principalTable: "docs_library_documents",
                        principalColumns: new[] { "organization_id", "library_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_docs_library_document_snippets_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "docs_public_document_snippets",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    public_source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    document_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    header = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    heading_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    language = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    preceding_text = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    identifiers = table.Column<string[]>(type: "text[]", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "halfvec(768)", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    embedding_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "setweight(to_tsvector('english', coalesce(heading_path, '')), 'A') ||\nsetweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(identifiers, ' '), '')), 'B') ||\nsetweight(to_tsvector('simple',  coalesce(language, '') || ' ' || coalesce(tag, '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(content, '')), 'D')", stored: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_docs_public_document_snippets", x => x.id);
                    table.ForeignKey(
                        name: "fk_docs_public_document_snippets_docs_public_documents_documen",
                        column: x => x.document_id,
                        principalSchema: "zeeq",
                        principalTable: "docs_public_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_document_snippets_document",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                columns: new[] { "organization_id", "library_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_library_document_snippets_organization_id_team_id",
                schema: "zeeq",
                table: "docs_library_document_snippets",
                columns: new[] { "organization_id", "team_id" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_document_snippets_document",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                columns: new[] { "public_source_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_docs_public_document_snippets_document_id",
                schema: "zeeq",
                table: "docs_public_document_snippets",
                column: "document_id");

            // ----------------------------------------------------------------------------------
            // Raw-SQL DDL the fluent API cannot express: halfvec storage, HNSW + GIN indexes, the
            // lease-aware partial index, and the reworked processing_status partial indexes that
            // now cover the 'Indexing' claim state. Both snippet tables are new/empty, so plain
            // CREATE INDEX inside the migration transaction is fine (no CONCURRENTLY needed).
            // ----------------------------------------------------------------------------------

            // Vectors do not compress; skip TOAST compression attempts (Cloud SQL guidance, PG13+).
            migrationBuilder.Sql(
                """
                ALTER TABLE zeeq.docs_library_document_snippets
                    ALTER COLUMN embedding SET STORAGE EXTERNAL;
                """
            );
            migrationBuilder.Sql(
                """
                ALTER TABLE zeeq.docs_public_document_snippets
                    ALTER COLUMN embedding SET STORAGE EXTERNAL;
                """
            );

            // HNSW vector index (cosine) on each table's embedding column.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_library_document_snippets_embedding
                    ON zeeq.docs_library_document_snippets
                    USING hnsw (embedding halfvec_cosine_ops) WITH (m = 16, ef_construction = 64);
                """
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_public_document_snippets_embedding
                    ON zeeq.docs_public_document_snippets
                    USING hnsw (embedding halfvec_cosine_ops) WITH (m = 16, ef_construction = 64);
                """
            );

            // btree_gin composite FTS index: distribution keys lead (KB data-modelling rule).
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_library_document_snippets_search
                    ON zeeq.docs_library_document_snippets
                    USING gin (organization_id, library_id, kind, search_vector);
                """
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_public_document_snippets_search
                    ON zeeq.docs_public_document_snippets
                    USING gin (public_source_id, kind, search_vector);
                """
            );

            // Identifier-overlap index for the exact-identifier boost arm.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_library_document_snippets_identifiers
                    ON zeeq.docs_library_document_snippets
                    USING gin (organization_id, library_id, identifiers);
                """
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_public_document_snippets_identifiers
                    ON zeeq.docs_public_document_snippets
                    USING gin (public_source_id, identifiers);
                """
            );

            // Embedding-pipeline source: rows needing vectors, without scanning the table.
            // embedding_started_at is in the payload (not the predicate) so lease-expiry reclaims
            // stay index-backed while the partial predicate keeps the index tiny.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_library_document_snippets_needs_embedding
                    ON zeeq.docs_library_document_snippets (organization_id, library_id, embedding_started_at)
                    WHERE embedding IS NULL;
                """
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_public_document_snippets_needs_embedding
                    ON zeeq.docs_public_document_snippets (public_source_id, embedding_started_at)
                    WHERE embedding IS NULL;
                """
            );

            // Rework the document processing_status partial indexes to cover the claim query,
            // which now selects Pending/Failed/Indexing rows (the sweep reclaims stale Indexing
            // rows). The private index was created in migration 20260630160324 and renamed in
            // 20260701123227; recreate it here and add the public-document equivalent.
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS zeeq.ix_docs_library_documents_processing_status;"
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_library_documents_processing_status
                    ON zeeq.docs_library_documents (organization_id, library_id, processing_status)
                    WHERE processing_status IN ('Pending', 'Failed', 'Indexing');
                """
            );
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS zeeq.ix_docs_public_documents_processing_status;"
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_public_documents_processing_status
                    ON zeeq.docs_public_documents (public_source_id, processing_status)
                    WHERE processing_status IN ('Pending', 'Failed', 'Indexing');
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the public-document processing_status index to its pre-migration absence and
            // the private index to its original Pending/Failed predicate.
            // NOTE: Down intentionally restores the PRE-migration baseline (Pending/Failed only,
            // created in 20260630160324) — 'Indexing' did not exist before this migration, so
            // reverting must not carry it back. This is correct rollback behavior, not a regression.
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS zeeq.ix_docs_public_documents_processing_status;"
            );
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS zeeq.ix_docs_library_documents_processing_status;"
            );
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_docs_library_documents_processing_status
                    ON zeeq.docs_library_documents (organization_id, library_id, processing_status)
                    WHERE processing_status IN ('Pending', 'Failed');
                """
            );

            migrationBuilder.DropTable(
                name: "docs_library_document_snippets",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "docs_public_document_snippets",
                schema: "zeeq");
        }
    }
}
