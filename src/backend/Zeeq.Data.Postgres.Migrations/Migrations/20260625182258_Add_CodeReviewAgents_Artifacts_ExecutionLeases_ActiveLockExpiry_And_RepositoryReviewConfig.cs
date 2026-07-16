using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class Add_CodeReviewAgents_Artifacts_ExecutionLeases_ActiveLockExpiry_And_RepositoryReviewConfig
        : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at_utc",
                schema: "zeeq",
                table: "code_review_active_locks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now() + interval '2 hours'"
            );

            migrationBuilder.CreateTable(
                name: "code_review_execution_leases",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    slot_index = table.Column<int>(type: "integer", nullable: false),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    lease_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    pull_request_record_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    pull_request_created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    code_review_record_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    code_review_created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    acquired_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    renewed_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    expires_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    worker_id = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
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
                        "pk_code_review_execution_leases",
                        x => new { x.organization_id, x.slot_index }
                    );
                    table.ForeignKey(
                        name: "fk_code_review_execution_leases_code_review_repositories_repos",
                        column: x => x.repository_id,
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_code_review_execution_leases_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "code_review_organization_settings",
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
                    max_concurrent_reviews = table.Column<int>(type: "integer", nullable: false),
                    execution_lease_duration = table.Column<TimeSpan>(
                        type: "interval",
                        nullable: false
                    ),
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
                    table.PrimaryKey("pk_code_review_organization_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_review_organization_settings_organizations_organizatio",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "code_reviewer_agents",
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
                    repository_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    display_name = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    review_facet = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    model_tier = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    prompt = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
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
                    activation_configuration = table.Column<string>(type: "jsonb", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_code_reviewer_agents", x => x.id);
                    table.ForeignKey(
                        name: "fk_code_reviewer_agents_code_review_repositories_repository_id",
                        column: x => x.repository_id,
                        principalSchema: "zeeq",
                        principalTable: "code_review_repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_code_reviewer_agents_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_code_reviewer_agents_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "storage_objects",
                schema: "zeeq",
                columns: table => new
                {
                    uri = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: false
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    container = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    path = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    content_text = table.Column<string>(type: "text", nullable: true),
                    content_bytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    expires_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_objects", x => x.uri);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_active_locks_expires_at_utc",
                schema: "zeeq",
                table: "code_review_active_locks",
                column: "expires_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_execution_leases_code_review_record_id",
                schema: "zeeq",
                table: "code_review_execution_leases",
                column: "code_review_record_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_execution_leases_lease_id",
                schema: "zeeq",
                table: "code_review_execution_leases",
                column: "lease_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_execution_leases_organization_id_expires_at_utc",
                schema: "zeeq",
                table: "code_review_execution_leases",
                columns: new[] { "organization_id", "expires_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_execution_leases_organization_id_renewed_at_utc",
                schema: "zeeq",
                table: "code_review_execution_leases",
                columns: new[] { "organization_id", "renewed_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_execution_leases_repository_id",
                schema: "zeeq",
                table: "code_review_execution_leases",
                column: "repository_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_organization_settings_organization_id",
                schema: "zeeq",
                table: "code_review_organization_settings",
                column: "organization_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_reviewer_agents_organization_id_repository_id_enabled_",
                schema: "zeeq",
                table: "code_reviewer_agents",
                columns: new[] { "organization_id", "repository_id", "enabled", "disabled_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_reviewer_agents_organization_id_team_id_repository_id",
                schema: "zeeq",
                table: "code_reviewer_agents",
                columns: new[] { "organization_id", "team_id", "repository_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_reviewer_agents_repository_id",
                schema: "zeeq",
                table: "code_reviewer_agents",
                column: "repository_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_storage_objects_container_path",
                schema: "zeeq",
                table: "storage_objects",
                columns: new[] { "container", "path" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_storage_objects_expires_at_utc",
                schema: "zeeq",
                table: "storage_objects",
                column: "expires_at_utc",
                filter: "expires_at_utc IS NOT NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_storage_objects_organization_id_container_created_at_utc",
                schema: "zeeq",
                table: "storage_objects",
                columns: new[] { "organization_id", "container", "created_at_utc" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "code_review_execution_leases", schema: "zeeq");

            migrationBuilder.DropTable(name: "code_review_organization_settings", schema: "zeeq");

            migrationBuilder.DropTable(name: "code_reviewer_agents", schema: "zeeq");

            migrationBuilder.DropTable(name: "storage_objects", schema: "zeeq");

            migrationBuilder.DropIndex(
                name: "ix_code_review_active_locks_expires_at_utc",
                schema: "zeeq",
                table: "code_review_active_locks"
            );

            migrationBuilder.DropColumn(
                name: "expires_at_utc",
                schema: "zeeq",
                table: "code_review_active_locks"
            );
        }
    }
}
