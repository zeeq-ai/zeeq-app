using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class LibraryDocument : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS unaccent;");
            // btree_gin lets the scalar distribution keys (organization_id, library_id) share a GIN
            // index with the search_vector / trigram columns, so search scans are scoped to a single
            // library instead of matching across every tenant and rechecking on the heap.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gin;");

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION zeeq.immutable_array_to_string(input_values text[], delimiter text)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE
                PARALLEL SAFE
                RETURNS NULL ON NULL INPUT
                AS $function$
                    SELECT array_to_string(input_values, delimiter);
                $function$;
                """
            );

            migrationBuilder.CreateTable(
                name: "libraries",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    description = table.Column<string>(
                        type: "character varying(2000)",
                        maxLength: 2000,
                        nullable: true
                    ),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    source_origin = table.Column<string>(type: "jsonb", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_libraries", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "fk_libraries_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_libraries_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "library_document",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    library_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    path = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: false
                    ),
                    path_reversed = table.Column<string>(
                        type: "text",
                        nullable: false,
                        computedColumnSql: "reverse(path)",
                        stored: true
                    ),
                    title = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    title_normalized = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    keywords = table.Column<string[]>(type: "text[]", nullable: false),
                    headings = table.Column<string[]>(type: "text[]", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(
                        type: "tsvector",
                        nullable: false,
                        computedColumnSql: "setweight(to_tsvector('english', coalesce(title, '')), 'A') ||\nsetweight(to_tsvector('simple',  coalesce(zeeq.immutable_array_to_string(keywords, ' '), '')), 'B') ||\nsetweight(to_tsvector('english', coalesce(zeeq.immutable_array_to_string(headings, ' '), '')), 'C') ||\nsetweight(to_tsvector('english', coalesce(content, '')), 'D')",
                        stored: true
                    ),
                    processing_status = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: true
                    ),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    source_origin = table.Column<string>(type: "jsonb", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_library_document",
                        x => new
                        {
                            x.organization_id,
                            x.library_id,
                            x.id,
                        }
                    );
                    table.ForeignKey(
                        name: "fk_library_document_libraries_organization_id_library_id",
                        columns: x => new { x.organization_id, x.library_id },
                        principalSchema: "zeeq",
                        principalTable: "libraries",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "fk_library_document_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_libraries_organization_id_name",
                schema: "zeeq",
                table: "libraries",
                columns: new[] { "organization_id", "name" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_libraries_organization_id_team_id",
                schema: "zeeq",
                table: "libraries",
                columns: new[] { "organization_id", "team_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_library_document_organization_id_team_id",
                schema: "zeeq",
                table: "library_document",
                columns: new[] { "organization_id", "team_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_library_document_path",
                schema: "zeeq",
                table: "library_document",
                columns: new[] { "organization_id", "library_id", "path" },
                unique: true
            );

            // Combined search filters by (organization_id, library_id) and then matches the
            // search_vector (@@) or the title trigram (%). Leading the GIN with the distribution
            // keys (via btree_gin) keeps each search scoped to the target library.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_library_document_search_vector
                    ON zeeq.library_document USING GIN (organization_id, library_id, search_vector);
                """
            );

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_library_document_path_reversed
                    ON zeeq.library_document (organization_id, library_id, path_reversed text_pattern_ops);
                """
            );

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_library_document_title_trgm
                    ON zeeq.library_document USING GIN (organization_id, library_id, title_normalized gin_trgm_ops);
                """
            );

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_library_document_processing_status
                    ON zeeq.library_document (organization_id, library_id, processing_status)
                    WHERE processing_status IN ('Pending', 'Failed');
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "library_document", schema: "zeeq");

            migrationBuilder.DropTable(name: "libraries", schema: "zeeq");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS zeeq.immutable_array_to_string(text[], text);"
            );

            // NOTE: pg_trgm and unaccent are shared database capabilities once enabled.
            // Rollback removes this feature's objects but intentionally leaves extensions installed.
        }
    }
}
