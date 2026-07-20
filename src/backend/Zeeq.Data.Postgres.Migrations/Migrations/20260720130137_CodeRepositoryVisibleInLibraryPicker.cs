using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class CodeRepositoryVisibleInLibraryPicker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "visible_in_library_picker",
                schema: "zeeq",
                table: "code_review_repositories",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "visible_in_library_picker",
                schema: "zeeq",
                table: "code_review_repositories");
        }
    }
}
