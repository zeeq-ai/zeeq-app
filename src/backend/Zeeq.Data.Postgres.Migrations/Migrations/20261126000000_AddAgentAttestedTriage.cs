using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Zeeq.Data.Postgres;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations;

/// <summary>Adds agent-reported triage provenance.</summary>
[DbContext(typeof(PostgresDbContext))]
[Migration("20261126000000_AddAgentAttestedTriage")]
public partial class AddAgentAttestedTriage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE zeeq.code_review_finding_triages ADD COLUMN confirmation_quote varchar(500);
            ALTER TABLE zeeq.code_review_finding_triages DROP CONSTRAINT ck_cr_triages_channel;
            ALTER TABLE zeeq.code_review_finding_triages ADD CONSTRAINT ck_cr_triages_channel
              CHECK (channel IN ('Ui', 'GitHub', 'Mcp', 'AgentAttested'));
            CREATE TABLE zeeq.code_review_finding_attestations (
              id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
              organization_id varchar(128) NOT NULL,
              triage_id varchar(128) NOT NULL,
              actor_user_id varchar(128),
              source_review_id varchar(128) NOT NULL,
              occurrence_key varchar(32) NOT NULL,
              reason text NOT NULL,
              auth_mode text,
              client_id text,
              confirmation_quote varchar(500) NOT NULL,
              agent_session_id varchar(128),
              confirmed_at_utc timestamptz NOT NULL,
              disposition varchar(32) NOT NULL
            );
            CREATE INDEX ix_cr_attestations_triage ON zeeq.code_review_finding_attestations (organization_id, triage_id);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE zeeq.code_review_finding_attestations;
            UPDATE zeeq.code_review_finding_triages SET channel = 'Mcp' WHERE channel = 'AgentAttested';
            ALTER TABLE zeeq.code_review_finding_triages DROP CONSTRAINT ck_cr_triages_channel;
            ALTER TABLE zeeq.code_review_finding_triages ADD CONSTRAINT ck_cr_triages_channel CHECK (channel IN ('Ui', 'GitHub', 'Mcp'));
            ALTER TABLE zeeq.code_review_finding_triages DROP COLUMN confirmation_quote;
            """);
    }
}
