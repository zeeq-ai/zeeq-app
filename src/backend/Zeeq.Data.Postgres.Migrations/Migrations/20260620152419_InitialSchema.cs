using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "zeeq");

            migrationBuilder
                .AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "auth_transient_states",
                schema: "zeeq",
                columns: table => new
                {
                    key = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    purpose = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    consumed_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_transient_states", x => new { x.purpose, x.key });
                }
            );

            migrationBuilder.CreateTable(
                name: "core_users",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    email = table.Column<string>(
                        type: "character varying(320)",
                        maxLength: 320,
                        nullable: true
                    ),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    picture_url = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: true
                    ),
                    last_login_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
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
                    table.PrimaryKey("pk_core_users", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_type = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    client_id = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: true
                    ),
                    client_secret = table.Column<string>(type: "text", nullable: true),
                    client_type = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    concurrency_token = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    consent_type = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    json_web_key_set = table.Column<string>(type: "text", nullable: true),
                    permissions = table.Column<string>(type: "text", nullable: true),
                    post_logout_redirect_uris = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redirect_uris = table.Column<string>(type: "text", nullable: true),
                    requirements = table.Column<string>(type: "text", nullable: true),
                    settings = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_open_iddict_applications", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    concurrency_token = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    description = table.Column<string>(type: "text", nullable: true),
                    descriptions = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    display_names = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    properties = table.Column<string>(type: "text", nullable: true),
                    resources = table.Column<string>(type: "text", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_open_iddict_scopes", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_user_identities",
                schema: "zeeq",
                columns: table => new
                {
                    provider = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    provider_subject = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    user_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    email = table.Column<string>(
                        type: "character varying(320)",
                        maxLength: 320,
                        nullable: true
                    ),
                    email_verified = table.Column<bool>(type: "boolean", nullable: false),
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    picture_url = table.Column<string>(
                        type: "character varying(2048)",
                        maxLength: 2048,
                        nullable: true
                    ),
                    first_seen_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    last_seen_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    disabled_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_auth_user_identities",
                        x => new { x.provider, x.provider_subject }
                    );
                    table.ForeignKey(
                        name: "fk_auth_user_identities_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "core_organizations",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    slug = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    icon_url = table.Column<string>(
                        type: "character varying(87380)",
                        maxLength: 87380,
                        nullable: true
                    ),
                    created_by_user_id = table.Column<string>(
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
                    table.PrimaryKey("pk_core_organizations", x => x.id);
                    table.ForeignKey(
                        name: "fk_core_organizations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    concurrency_token = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    creation_date = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    properties = table.Column<string>(type: "text", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    subject = table.Column<string>(
                        type: "character varying(400)",
                        maxLength: 400,
                        nullable: true
                    ),
                    type = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_open_iddict_authorizations", x => x.id);
                    table.ForeignKey(
                        name: "fk_open_iddict_authorizations_open_iddict_applications_application",
                        column: x => x.application_id,
                        principalSchema: "zeeq",
                        principalTable: "OpenIddictApplications",
                        principalColumn: "id"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "core_organization_memberships",
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
                    user_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    role = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    status = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    invited_email = table.Column<string>(
                        type: "character varying(320)",
                        maxLength: 320,
                        nullable: true
                    ),
                    created_by_user_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    disabled_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_core_organization_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_core_organization_memberships_core_organizations_organizati",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_core_organization_memberships_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_core_organization_memberships_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "core_teams",
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
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    is_root_team = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<string>(
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
                    table.PrimaryKey("pk_core_teams", x => x.id);
                    table.UniqueConstraint(
                        "ak_teams_organization_id_id",
                        x => new { x.organization_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_core_teams_core_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_core_teams_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "OpenIddictTokens",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    authorization_id = table.Column<string>(type: "text", nullable: true),
                    concurrency_token = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    creation_date = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    expiration_date = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    payload = table.Column<string>(type: "text", nullable: true),
                    properties = table.Column<string>(type: "text", nullable: true),
                    redemption_date = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    reference_id = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: true
                    ),
                    status = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    subject = table.Column<string>(
                        type: "character varying(400)",
                        maxLength: 400,
                        nullable: true
                    ),
                    type = table.Column<string>(
                        type: "character varying(150)",
                        maxLength: 150,
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_open_iddict_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_open_iddict_tokens_open_iddict_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "zeeq",
                        principalTable: "OpenIddictApplications",
                        principalColumn: "id"
                    );
                    table.ForeignKey(
                        name: "fk_open_iddict_tokens_open_iddict_authorizations_authorization_id",
                        column: x => x.authorization_id,
                        principalSchema: "zeeq",
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "id"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_client_credentials",
                schema: "zeeq",
                columns: table => new
                {
                    client_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    owner_user_id = table.Column<string>(
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
                        nullable: false
                    ),
                    owner_provider = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    owner_provider_subject = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    client_secret = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    selected_partition_ids_json = table.Column<string>(
                        type: "text",
                        nullable: false
                    ),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    revoked_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_client_credentials", x => x.client_id);
                    table.ForeignKey(
                        name: "fk_auth_client_credentials_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_auth_client_credentials_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_auth_client_credentials_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_dcr_client_setups",
                schema: "zeeq",
                columns: table => new
                {
                    client_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    status = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    client_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    redirect_uris_json = table.Column<string>(type: "text", nullable: false),
                    requested_scopes = table.Column<string>(
                        type: "character varying(1024)",
                        maxLength: 1024,
                        nullable: false
                    ),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    expires_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    claimed_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    claimed_user_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    selected_partition_ids_json = table.Column<string>(
                        type: "text",
                        nullable: false
                    ),
                    claimed_owner_provider = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    claimed_owner_provider_subject = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: true
                    ),
                    revoked_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_dcr_client_setups", x => x.client_id);
                    table.ForeignKey(
                        name: "fk_auth_dcr_client_setups_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_auth_dcr_client_setups_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_auth_dcr_client_setups_users_claimed_user_id",
                        column: x => x.claimed_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "auth_user_tokens",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    owner_user_id = table.Column<string>(
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
                        nullable: false
                    ),
                    owner_provider = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    owner_provider_subject = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: false
                    ),
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    selected_partition_ids_json = table.Column<string>(
                        type: "text",
                        nullable: false
                    ),
                    created_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    expires_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    revoked_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    last_used_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_user_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_user_tokens_core_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_auth_user_tokens_core_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_auth_user_tokens_core_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "core_partitions",
                schema: "zeeq",
                columns: table => new
                {
                    id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    scope_type = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
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
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
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
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_core_partitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_core_partitions_core_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_core_partitions_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "core_team_memberships",
                schema: "zeeq",
                columns: table => new
                {
                    organization_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    team_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    user_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    role = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<string>(
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
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_core_team_memberships",
                        x => new
                        {
                            x.organization_id,
                            x.team_id,
                            x.user_id,
                        }
                    );
                    table.ForeignKey(
                        name: "fk_core_team_memberships_core_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "zeeq",
                        principalTable: "core_organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_core_team_memberships_core_teams_organization_id_team_id",
                        columns: x => new { x.organization_id, x.team_id },
                        principalSchema: "zeeq",
                        principalTable: "core_teams",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_core_team_memberships_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_core_team_memberships_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "zeeq",
                        principalTable: "core_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_client_credentials_organization_id_team_id_owner_user_",
                schema: "zeeq",
                table: "auth_client_credentials",
                columns: new[] { "organization_id", "team_id", "owner_user_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_client_credentials_owner_user_id",
                schema: "zeeq",
                table: "auth_client_credentials",
                column: "owner_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_dcr_client_setups_claimed_user_id",
                schema: "zeeq",
                table: "auth_dcr_client_setups",
                column: "claimed_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_dcr_client_setups_organization_id_team_id_claimed_user",
                schema: "zeeq",
                table: "auth_dcr_client_setups",
                columns: new[] { "organization_id", "team_id", "claimed_user_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_dcr_client_setups_status_expires_at_utc",
                schema: "zeeq",
                table: "auth_dcr_client_setups",
                columns: new[] { "status", "expires_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_transient_states_purpose_expires_at_utc_consumed_at_utc",
                schema: "zeeq",
                table: "auth_transient_states",
                columns: new[] { "purpose", "expires_at_utc", "consumed_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_user_identities_user_id",
                schema: "zeeq",
                table: "auth_user_identities",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_user_tokens_organization_id_team_id_owner_user_id",
                schema: "zeeq",
                table: "auth_user_tokens",
                columns: new[] { "organization_id", "team_id", "owner_user_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_auth_user_tokens_owner_user_id",
                schema: "zeeq",
                table: "auth_user_tokens",
                column: "owner_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_created_by_user_id",
                schema: "zeeq",
                table: "core_organization_memberships",
                column: "created_by_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_disabled_at_utc",
                schema: "zeeq",
                table: "core_organization_memberships",
                column: "disabled_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_invited_email_status",
                schema: "zeeq",
                table: "core_organization_memberships",
                columns: new[] { "invited_email", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_organization_id_status",
                schema: "zeeq",
                table: "core_organization_memberships",
                columns: new[] { "organization_id", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_organization_id_user_id",
                schema: "zeeq",
                table: "core_organization_memberships",
                columns: new[] { "organization_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL AND status = 'Active'"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_user_id_is_default",
                schema: "zeeq",
                table: "core_organization_memberships",
                columns: new[] { "user_id", "is_default" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organization_memberships_user_id_status",
                schema: "zeeq",
                table: "core_organization_memberships",
                columns: new[] { "user_id", "status" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organizations_created_by_user_id",
                schema: "zeeq",
                table: "core_organizations",
                column: "created_by_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organizations_disabled_at_utc",
                schema: "zeeq",
                table: "core_organizations",
                column: "disabled_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_organizations_slug",
                schema: "zeeq",
                table: "core_organizations",
                column: "slug",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_partitions_disabled_at_utc",
                schema: "zeeq",
                table: "core_partitions",
                column: "disabled_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_partitions_organization_id_team_id",
                schema: "zeeq",
                table: "core_partitions",
                columns: new[] { "organization_id", "team_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_partitions_scope_type_organization_id_team_id",
                schema: "zeeq",
                table: "core_partitions",
                columns: new[] { "scope_type", "organization_id", "team_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_team_memberships_created_by_user_id",
                schema: "zeeq",
                table: "core_team_memberships",
                column: "created_by_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_team_memberships_organization_id_team_id_disabled_at_u",
                schema: "zeeq",
                table: "core_team_memberships",
                columns: new[] { "organization_id", "team_id", "disabled_at_utc" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_team_memberships_user_id_organization_id",
                schema: "zeeq",
                table: "core_team_memberships",
                columns: new[] { "user_id", "organization_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_teams_created_by_user_id",
                schema: "zeeq",
                table: "core_teams",
                column: "created_by_user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_teams_disabled_at_utc",
                schema: "zeeq",
                table: "core_teams",
                column: "disabled_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_users_disabled_at_utc",
                schema: "zeeq",
                table: "core_users",
                column: "disabled_at_utc"
            );

            migrationBuilder.CreateIndex(
                name: "ix_core_users_email",
                schema: "zeeq",
                table: "core_users",
                column: "email"
            );

            migrationBuilder.CreateIndex(
                name: "ix_open_iddict_applications_client_id",
                schema: "zeeq",
                table: "OpenIddictApplications",
                column: "client_id",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_open_iddict_authorizations_application_id_status_subject_type",
                schema: "zeeq",
                table: "OpenIddictAuthorizations",
                columns: new[] { "application_id", "status", "subject", "type" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_open_iddict_scopes_name",
                schema: "zeeq",
                table: "OpenIddictScopes",
                column: "name",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_open_iddict_tokens_application_id_status_subject_type",
                schema: "zeeq",
                table: "OpenIddictTokens",
                columns: new[] { "application_id", "status", "subject", "type" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_open_iddict_tokens_authorization_id",
                schema: "zeeq",
                table: "OpenIddictTokens",
                column: "authorization_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_open_iddict_tokens_reference_id",
                schema: "zeeq",
                table: "OpenIddictTokens",
                column: "reference_id",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "auth_client_credentials", schema: "zeeq");

            migrationBuilder.DropTable(name: "auth_dcr_client_setups", schema: "zeeq");

            migrationBuilder.DropTable(name: "auth_transient_states", schema: "zeeq");

            migrationBuilder.DropTable(name: "auth_user_identities", schema: "zeeq");

            migrationBuilder.DropTable(name: "auth_user_tokens", schema: "zeeq");

            migrationBuilder.DropTable(name: "core_organization_memberships", schema: "zeeq");

            migrationBuilder.DropTable(name: "core_partitions", schema: "zeeq");

            migrationBuilder.DropTable(name: "core_team_memberships", schema: "zeeq");

            migrationBuilder.DropTable(name: "OpenIddictScopes", schema: "zeeq");

            migrationBuilder.DropTable(name: "OpenIddictTokens", schema: "zeeq");

            migrationBuilder.DropTable(name: "core_teams", schema: "zeeq");

            migrationBuilder.DropTable(name: "OpenIddictAuthorizations", schema: "zeeq");

            migrationBuilder.DropTable(name: "core_organizations", schema: "zeeq");

            migrationBuilder.DropTable(name: "OpenIddictApplications", schema: "zeeq");

            migrationBuilder.DropTable(name: "core_users", schema: "zeeq");
        }
    }
}
