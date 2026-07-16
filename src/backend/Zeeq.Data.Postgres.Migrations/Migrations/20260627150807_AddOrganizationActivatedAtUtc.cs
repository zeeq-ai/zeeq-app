using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationActivatedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "activated_at_utc",
                schema: "zeeq",
                table: "core_organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE zeeq.core_organizations
                SET activated_at_utc = created_at_utc
                WHERE activated_at_utc IS NULL
                  AND disabled_at_utc IS NULL
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "activated_at_utc",
                schema: "zeeq",
                table: "core_organizations");
        }
    }
}
