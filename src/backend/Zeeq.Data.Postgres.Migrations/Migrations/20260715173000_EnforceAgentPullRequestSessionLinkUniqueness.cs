using Zeeq.Data.Postgres;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations;

/// <inheritdoc />
[DbContext(typeof(PostgresDbContext))]
[Migration("20260715173000_EnforceAgentPullRequestSessionLinkUniqueness")]
public partial class EnforceAgentPullRequestSessionLinkUniqueness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            WITH ranked_links AS (
                SELECT id,
                       row_number() OVER (
                           PARTITION BY organization_id, pull_request_record_id, conversation_id
                           ORDER BY
                               is_pending ASC,
                               CASE link_origin
                                   WHEN 'UserCurated' THEN 0
                                   WHEN 'WebhookCurated' THEN 1
                                   WHEN 'PrMarker' THEN 2
                                   ELSE 3
                               END,
                               linked_at_utc ASC NULLS LAST,
                               id ASC
                       ) AS row_number
                FROM zeeq.agent_pull_request_session_links
            )
            DELETE FROM zeeq.agent_pull_request_session_links links
            USING ranked_links
            WHERE links.id = ranked_links.id AND ranked_links.row_number > 1;
            """
        );
        migrationBuilder.DropIndex(
            name: "ix_agent_pull_request_session_links_organization_id_pull_reque",
            schema: "zeeq",
            table: "agent_pull_request_session_links"
        );
        migrationBuilder.CreateIndex(
            name: "ix_agent_pull_request_session_links_organization_id_pull_reque",
            schema: "zeeq",
            table: "agent_pull_request_session_links",
            columns: new[] { "organization_id", "pull_request_record_id", "conversation_id" },
            unique: true
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_agent_pull_request_session_links_organization_id_pull_reque",
            schema: "zeeq",
            table: "agent_pull_request_session_links"
        );
        migrationBuilder.CreateIndex(
            name: "ix_agent_pull_request_session_links_organization_id_pull_reque",
            schema: "zeeq",
            table: "agent_pull_request_session_links",
            columns: new[] { "organization_id", "pull_request_record_id", "conversation_id" }
        );
    }
}
