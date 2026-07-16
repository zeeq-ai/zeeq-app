using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "messaging");

            migrationBuilder.AddColumn<string>(
                name: "tier",
                schema: "zeeq",
                table: "core_organizations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Default"
            );

            CreateQueueTable(migrationBuilder, "brighter_messages_priority");
            CreateQueueTable(migrationBuilder, "brighter_messages");
            CreateQueueTable(migrationBuilder, "brighter_messages_low");
            CreateQueueTable(migrationBuilder, "brighter_messages_system");

            migrationBuilder.CreateTable(
                name: "brighter_messages_dead",
                schema: "messaging",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    original_queue = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: false
                    ),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "text", nullable: false),
                    attempt_count = table.Column<int>(
                        type: "integer",
                        nullable: false,
                        defaultValue: 1
                    ),
                    dead_lettered_at = table.Column<DateTime>(
                        type: "timestamp without time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                    created_at = table.Column<DateTime>(
                        type: "timestamp without time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brighter_messages_dead", row => row.id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_brighter_messages_dead_original_queue_dead_lettered_at",
                schema: "messaging",
                table: "brighter_messages_dead",
                columns: ["original_queue", "dead_lettered_at"]
            );

            static void CreateQueueTable(MigrationBuilder migrationBuilder, string tableName)
            {
                migrationBuilder.CreateTable(
                    name: tableName,
                    schema: "messaging",
                    columns: table => new
                    {
                        id = table
                            .Column<long>(type: "bigint", nullable: false)
                            .Annotation(
                                "Npgsql:ValueGenerationStrategy",
                                NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                            ),
                        visible_timeout = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: false,
                            defaultValueSql: "CURRENT_TIMESTAMP"
                        ),
                        queue = table.Column<string>(
                            type: "character varying(255)",
                            maxLength: 255,
                            nullable: false
                        ),
                        content = table.Column<string>(type: "jsonb", nullable: false),
                        created_at = table.Column<DateTime>(
                            type: "timestamp without time zone",
                            nullable: false,
                            defaultValueSql: "CURRENT_TIMESTAMP"
                        ),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey($"pk_{tableName}", row => row.id);
                    }
                );

                migrationBuilder.CreateIndex(
                    name: $"ix_{tableName}_queue_visible_timeout",
                    schema: "messaging",
                    table: tableName,
                    columns: ["queue", "visible_timeout"]
                );
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "brighter_messages_dead", schema: "messaging");
            migrationBuilder.DropTable(name: "brighter_messages_system", schema: "messaging");
            migrationBuilder.DropTable(name: "brighter_messages_low", schema: "messaging");
            migrationBuilder.DropTable(name: "brighter_messages", schema: "messaging");
            migrationBuilder.DropTable(name: "brighter_messages_priority", schema: "messaging");
            migrationBuilder.DropSchema(name: "messaging");

            migrationBuilder.DropColumn(
                name: "tier",
                schema: "zeeq",
                table: "core_organizations"
            );
        }
    }
}
