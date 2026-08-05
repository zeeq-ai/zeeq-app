using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Add_World_Model_Afa_Storage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "awm_revision_seq",
                schema: "zeeq");

            migrationBuilder.CreateTable(
                name: "awm_nodes",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    team_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    segment = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    obsolete = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    is_effectively_obsolete = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    semantic_revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_awm_nodes", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_awm_nodes_kind", "kind IN ('area', 'feature', 'action')");
                    table.CheckConstraint("ck_awm_nodes_semantic_revision", "semantic_revision >= 1");
                    table.CheckConstraint("ck_awm_nodes_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_awm_nodes_awm_nodes_organization_id_parent_id",
                        columns: x => new { x.organization_id, x.parent_id },
                        principalSchema: "zeeq",
                        principalTable: "awm_nodes",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "awm_body_items",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    participants = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    obsolete = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('zeeq.awm_revision_seq')"),
                    repo_pr_sha = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_awm_body_items", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_awm_body_items_kind", "kind IN ('rule', 'flow')");
                    table.CheckConstraint("ck_awm_body_items_revision", "revision >= 1");
                    table.ForeignKey(
                        name: "fk_awm_body_items_awm_nodes_organization_id_node_id",
                        columns: x => new { x.organization_id, x.node_id },
                        principalSchema: "zeeq",
                        principalTable: "awm_nodes",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_awm_body_items_organization_id_node_id_kind",
                schema: "zeeq",
                table: "awm_body_items",
                columns: new[] { "organization_id", "node_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_awm_body_items_participants",
                schema: "zeeq",
                table: "awm_body_items",
                columns: new[] { "organization_id", "participants" })
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_awm_nodes_organization_id_parent_id_segment",
                schema: "zeeq",
                table: "awm_nodes",
                columns: new[] { "organization_id", "parent_id", "segment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_awm_nodes_organization_id_path",
                schema: "zeeq",
                table: "awm_nodes",
                columns: new[] { "organization_id", "path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "awm_body_items",
                schema: "zeeq");

            migrationBuilder.DropTable(
                name: "awm_nodes",
                schema: "zeeq");

            migrationBuilder.DropSequence(
                name: "awm_revision_seq",
                schema: "zeeq");
        }
    }
}
