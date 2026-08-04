using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Postgres implementation of finite conversation rollup backfill.
/// </summary>
internal sealed class PostgresAgentConversationRollupBackfillStore(PostgresDbContext db)
    : IAgentConversationRollupBackfillStore
{
    /// <inheritdoc />
    public async Task<AgentConversationRollupBackfillResult> BackfillNextAsync(
        int targetVersion,
        TimeSpan statementTimeout,
        IReadOnlySet<AgentConversationKey> excludedKeys,
        CancellationToken cancellationToken
    )
    {
        AgentConversationRollupBackfillSqlRow? claimed = null;

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                cancellationToken
            );

            var statementTimeoutMs = Math.Max(1, (int)statementTimeout.TotalMilliseconds);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('statement_timeout', {statementTimeoutMs.ToString()}, true)",
                cancellationToken
            );

            claimed = await ClaimNextAsync(targetVersion, excludedKeys, cancellationToken);
            if (claimed is null)
            {
                await transaction.CommitAsync(cancellationToken);

                return new(AgentConversationRollupBackfillStatus.NoWork);
            }

            await RecomputeClaimedAsync(targetVersion, claimed, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new(
                AgentConversationRollupBackfillStatus.Completed,
                new AgentConversationKey(claimed.OrganizationId, claimed.ConversationId)
            );
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            return new(
                AgentConversationRollupBackfillStatus.TimedOut,
                claimed is null
                    ? null
                    : new AgentConversationKey(claimed.OrganizationId, claimed.ConversationId)
            );
        }
    }

    private async Task<AgentConversationRollupBackfillSqlRow?> ClaimNextAsync(
        int targetVersion,
        IReadOnlySet<AgentConversationKey> excludedKeys,
        CancellationToken cancellationToken
    )
    {
        var excludedRows = excludedKeys
            .Select(key => new AgentConversationRollupBackfillExcludedKey(
                key.OrganizationId,
                key.ConversationId
            ))
            .ToArray();
        var excludedJson = JsonSerializer.Serialize(
            excludedRows,
            PostgresAgentTelemetryJsonContext
                .Default
                .AgentConversationRollupBackfillExcludedKeyArray
        );

        FormattableString sql = $"""
            /* telemetry.agent_conversations.backfill_claim */
            SELECT
                conversation.organization_id,
                conversation.id AS conversation_id
            FROM zeeq.agent_conversations AS conversation
            WHERE
                conversation.rollup_version < {targetVersion}
                AND NOT EXISTS (
                    SELECT 1
                    FROM jsonb_to_recordset(CAST({excludedJson} AS jsonb)) AS excluded(
                        organization_id text,
                        conversation_id text
                    )
                    WHERE
                        excluded.organization_id = conversation.organization_id
                        AND excluded.conversation_id = conversation.id
                )
            ORDER BY
                conversation.rollup_version,
                conversation.started_at_utc,
                conversation.organization_id,
                conversation.id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """;

        return await db
            .Database.SqlQuery<AgentConversationRollupBackfillSqlRow>(sql)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task RecomputeClaimedAsync(
        int targetVersion,
        AgentConversationRollupBackfillSqlRow claimed,
        CancellationToken cancellationToken
    )
    {
        FormattableString sql = $"""
            /* telemetry.agent_conversations.backfill_recompute */
            WITH candidate AS (
                SELECT
                    organization_id,
                    id,
                    started_at_utc,
                    completed_at_utc
                FROM zeeq.agent_conversations
                WHERE
                    organization_id = {claimed.OrganizationId}
                    AND id = {claimed.ConversationId}
                    AND rollup_version < {targetVersion}
            ),
            prompt_title AS (
                SELECT left(event.prompt_text, 200) AS title
                FROM candidate
                JOIN zeeq.agent_session_events AS event
                    ON event.organization_id = candidate.organization_id
                    AND event.conversation_id = candidate.id
                    AND event.event_type = {(byte)AgentSessionEventType.Prompt}
                    AND event.is_housekeeping = false
                    AND NULLIF(btrim(event.prompt_text), '') IS NOT NULL
                ORDER BY
                    event.occurred_at_utc,
                    event.source_sequence NULLS LAST,
                    event.id
                LIMIT 1
            ),
            completion_rollup AS (
                SELECT
                    COALESCE(SUM(COALESCE(event.input_tokens, 0)), 0)::bigint
                        AS total_input_tokens,
                    COALESCE(SUM(COALESCE(event.output_tokens, 0)), 0)::bigint
                        AS total_output_tokens,
                    COUNT(event.id)::bigint AS completion_count,
                    COUNT(*) FILTER (
                        WHERE event.id IS NOT NULL AND event.cost_usd IS NULL
                    )::bigint
                        AS missing_cost_completion_count,
                    COALESCE(SUM(COALESCE(event.cost_usd, 0)), 0)::numeric
                        AS known_cost_usd
                FROM candidate
                LEFT JOIN zeeq.agent_session_events AS event
                    ON event.organization_id = candidate.organization_id
                    AND event.conversation_id = candidate.id
                    AND event.event_type = {(byte)AgentSessionEventType.Completion}
            )
            UPDATE zeeq.agent_conversations AS conversation
            SET
                title = COALESCE(conversation.title, prompt_title.title),
                total_input_tokens = completion_rollup.total_input_tokens,
                total_output_tokens = completion_rollup.total_output_tokens,
                missing_cost_completion_count =
                    completion_rollup.missing_cost_completion_count,
                total_cost_usd = CASE
                    WHEN completion_rollup.missing_cost_completion_count > 0 THEN NULL
                    WHEN completion_rollup.completion_count = 0 THEN NULL
                    ELSE completion_rollup.known_cost_usd
                END,
                rollup_version = {targetVersion}
            FROM candidate
            CROSS JOIN completion_rollup
            LEFT JOIN prompt_title ON true
            WHERE
                conversation.organization_id = candidate.organization_id
                AND conversation.id = candidate.id
            """;

        await db.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken);
    }
}
