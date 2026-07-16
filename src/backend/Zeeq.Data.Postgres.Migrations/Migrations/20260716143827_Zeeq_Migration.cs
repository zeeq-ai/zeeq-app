using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Zeeq_Migration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_user_email",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_user_email_c");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_tool_name_",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_tool_name_cr");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_repository",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_repository_i");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_library_cr",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_library_crea");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_facet_crea",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_facet_create");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_created_at",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_created_at_u");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_user_email_c",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_user_email");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_tool_name_cr",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_tool_name_");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_repository_i",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_repository");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_library_crea",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_library_cr");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_facet_create",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_facet_crea");

            migrationBuilder.RenameIndex(
                name: "ix_zeeq_metric_events_organization_id_metric_type_created_at_u",
                schema: "zeeq",
                table: "zeeq_metric_events",
                newName: "ix_zeeq_metric_events_organization_id_metric_type_created_at");
        }
    }
}
