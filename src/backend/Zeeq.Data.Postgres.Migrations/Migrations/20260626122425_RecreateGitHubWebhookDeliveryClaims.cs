using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class RecreateGitHubWebhookDeliveryClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SELECT cron.unschedule(jobid)
                FROM cron.job
                WHERE jobname = 'code-review-github-webhook-delivery-claim-retention';
                """
            );

            migrationBuilder.DropTable(
                name: "code_review_github_webhook_deliveries",
                schema: "zeeq"
            );

            migrationBuilder
                .CreateTable(
                    name: "code_review_github_webhook_delivery_claims",
                    schema: "zeeq",
                    columns: table => new
                    {
                        delivery_id = table.Column<string>(
                            type: "character varying(128)",
                            maxLength: 128,
                            nullable: false
                        ),
                        claimed_at_utc = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: false
                        ),
                        processed_at_utc = table.Column<DateTimeOffset>(
                            type: "timestamp with time zone",
                            nullable: true
                        ),
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey(
                            "pk_code_review_github_webhook_delivery_claims",
                            row => row.delivery_id
                        );
                    }
                )
                .Annotation("Npgsql:UnloggedTable", true);

            migrationBuilder.Sql(
                """
                ALTER TABLE zeeq.code_review_github_webhook_delivery_claims SET UNLOGGED;
                """
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_webhook_delivery_claims_claimed_at_utc",
                schema: "zeeq",
                table: "code_review_github_webhook_delivery_claims",
                column: "claimed_at_utc"
            );

            migrationBuilder.Sql(
                """
                SELECT cron.schedule(
                    'code-review-github-webhook-delivery-claim-retention',
                    '*/5 * * * *',
                    $job$
                    DELETE FROM zeeq.code_review_github_webhook_delivery_claims
                    WHERE delivery_id IN (
                        SELECT delivery_id
                        FROM zeeq.code_review_github_webhook_delivery_claims
                        WHERE claimed_at_utc < now() - interval '7 days'
                        ORDER BY claimed_at_utc
                        LIMIT 10000
                    );
                    $job$
                );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SELECT cron.unschedule(jobid)
                FROM cron.job
                WHERE jobname = 'code-review-github-webhook-delivery-claim-retention';
                """
            );

            migrationBuilder.DropTable(
                name: "code_review_github_webhook_delivery_claims",
                schema: "zeeq"
            );

            migrationBuilder.CreateTable(
                name: "code_review_github_webhook_deliveries",
                schema: "zeeq",
                columns: table => new
                {
                    delivery_id = table.Column<string>(
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
                        nullable: true
                    ),
                    event_name = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    action = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    received_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    processed_at_utc = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    status = table.Column<string>(
                        type: "character varying(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    failure_message = table.Column<string>(
                        type: "character varying(4096)",
                        maxLength: 4096,
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey(
                        "pk_code_review_github_webhook_deliveries",
                        row => row.delivery_id
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_code_review_github_webhook_deliveries_organization_id_recei",
                schema: "zeeq",
                table: "code_review_github_webhook_deliveries",
                columns: new[] { "organization_id", "received_at_utc" }
            );
        }
    }
}
