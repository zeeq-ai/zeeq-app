using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmSettingsEncryptedValues : Migration
    {
        private const string DefaultLlmConfigurationJson = """
            {
              "Fast": {
                "Provider": "DeepSeek",
                "Model": "deepseek-v4-flash",
                "KeyId": null
              },
              "High": {
                "Provider": "DeepSeek",
                "Model": "deepseek-v4-pro",
                "KeyId": null
              },
              "Max": {
                "Provider": "DeepSeek",
                "Model": "deepseek-v4-pro",
                "KeyId": null
              }
            }
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "llm_configuration",
                schema: "zeeq",
                table: "core_organizations",
                type: "jsonb",
                nullable: false,
                defaultValue: DefaultLlmConfigurationJson
            );

            migrationBuilder.CreateTable(
                name: "core_encrypted_values",
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
                    kind = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    encryption_provider = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    disabled_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_core_encrypted_values",
                        x => new { x.organization_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_core_encrypted_values_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_encrypted_values_disabled_at_utc",
                schema: "zeeq",
                table: "core_encrypted_values",
                column: "disabled_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_encrypted_values_organization_id_kind_disabled_at_utc",
                schema: "zeeq",
                table: "core_encrypted_values",
                columns: new[] { "organization_id", "kind", "disabled_at_utc" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "core_encrypted_values", schema: "zeeq");

            migrationBuilder.DropColumn(
                name: "llm_configuration",
                schema: "zeeq",
                table: "core_organizations"
            );
        }
    }
}
