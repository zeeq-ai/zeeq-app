using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationActivationKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "core_organization_activation_keys",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    activated_organization_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    activated_by_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_core_organization_activation_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_core_organization_activation_keys_organizations_activated_o",
                        column: x => x.activated_organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_core_organization_activation_keys_users_activated_by_user_id",
                        column: x => x.activated_by_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_core_organization_activation_keys_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_activated_at_utc",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "activated_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_activated_by_user_id",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "activated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_activated_organization_id",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "activated_organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_created_at_utc",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_created_by_user_id",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_disabled_at_utc",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "disabled_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_expires_at_utc",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_activation_keys_key_hash",
                schema: "zeeq",
                table: "core_organization_activation_keys",
                column: "key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "core_organization_activation_keys",
                schema: "zeeq");
        }
    }
}
