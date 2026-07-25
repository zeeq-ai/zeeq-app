using System.Buffers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeeq.Core.Models;

namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Postgres implementation of <see cref="IAgentTelemetryDomainStore"/>.
/// Upserts conversations, appends events, and acknowledges raw rows inside an
/// explicit transaction — domain writes commit first, raw deletion follows,
/// then the transaction commits so a failure at any stage rolls back.
/// </summary>
internal sealed class PostgresAgentTelemetryDomainStore(PostgresDbContext db)
    : IAgentTelemetryDomainStore
{
    private const int EventInsertChunkSize = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentConversation>> FindForRepositoryBranchAsync(
        string organizationId,
        string ownerQualifiedRepoName,
        string branch,
        CancellationToken cancellationToken
    )
    {
        var canonicalRepository = TelemetryRepositoryIdentity.Normalize(ownerQualifiedRepoName);
        if (canonicalRepository.Length == 0)
        {
            return [];
        }

        return await db.Set<AgentConversation>()
            .AsNoTracking()
            .Where(conversation =>
                conversation.OrganizationId == organizationId
                && conversation.HeadBranch == branch
                && conversation.RepoRemoteUrl == canonicalRepository
            )
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryCreatePullRequestSessionLinkAsync(
        AgentPullRequestSessionLink link,
        CancellationToken cancellationToken
    )
    {
        db.Set<AgentPullRequestSessionLink>().Add(link);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsPullRequestSessionLinkDuplicate(exception))
        {
            db.Entry(link).State = EntityState.Detached;

            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<AgentTelemetryDomainWriteResult> UpsertConversationsEventsAndAcknowledgeRawAsync(
        IEnumerable<AgentConversation> conversations,
        IEnumerable<AgentSessionEvent> events,
        IReadOnlyList<TelemetryRawRequest> rawRows,
        CancellationToken cancellationToken
    )
    {
        var newKeys = new HashSet<AgentConversationKey>();
        var eventRows = events.ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // NOTE: This N+1 existence check per conversation will be replaced in a
        // later slice (Phase 13) with a single-round-trip xmax = 0 SQL upsert
        // that returns newly inserted keys directly.

        foreach (var conversation in conversations)
        {
            conversation.RepoRemoteUrl = CanonicalRepositoryOrNull(conversation.RepoRemoteUrl);

            var existing = await db.Set<AgentConversation>()
                .FirstOrDefaultAsync(
                    c => c.Id == conversation.Id && c.OrganizationId == conversation.OrganizationId,
                    cancellationToken
                );

            if (existing is null)
            {
                db.Set<AgentConversation>().Add(conversation);

                newKeys.Add(new(conversation.OrganizationId, conversation.Id));
            }
            else
            {
                existing.MergeFrom(conversation);
            }
        }

        // Flush domain writes inside the open transaction; raw-row deletion and
        // CommitAsync follow — both commit atomically.
        await db.SaveChangesAsync(cancellationToken);

        var newEventIds = await InsertNewEventsAsync(eventRows, cancellationToken);

        foreach (var raw in rawRows)
        {
            var deleted = await db.Set<TelemetryRawRequest>()
                .Where(r => r.Id == raw.Id && r.ProcessingLeaseId == raw.ProcessingLeaseId)
                .ExecuteDeleteAsync(cancellationToken);

            // If another worker reclaimed the row after lease expiry, abort so
            // this worker's domain writes are rolled back and the row is
            // retried by the current lease holder.
            if (deleted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                return new(
                    new HashSet<AgentConversationKey>(),
                    new HashSet<string>(StringComparer.Ordinal)
                );
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new(newKeys, newEventIds);
    }

    private async Task<IReadOnlySet<string>> InsertNewEventsAsync(
        IReadOnlyList<AgentSessionEvent> events,
        CancellationToken cancellationToken
    )
    {
        var inserted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chunk in events.Chunk(EventInsertChunkSize))
        {
            // NOTE: Use database conflict handling rather than a pre-read duplicate check.
            // Multiple workers can process duplicate raw telemetry concurrently; the
            // partitioned table's primary key is the authoritative idempotency decision.
            // NOTE: 100 events is the compromise point: still set-wise enough to avoid
            // per-event round trips, but bounded so direct imports and dense OTLP raw
            // rows do not produce oversized JSON parameters or long partition inserts.
            var rows = chunk.Select(ToInsertRow).ToArray();
            var rowsJson = JsonSerializer.Serialize(
                rows,
                PostgresAgentTelemetryJsonContext.Default.AgentSessionEventInsertRowArray
            );
            EnsureInsertJsonContract(rowsJson);

            FormattableString sql = $"""
                WITH rows AS (
                    SELECT *
                    FROM jsonb_to_recordset(CAST({rowsJson} AS jsonb)) AS row(
                        -- NOTE: These column names are the database-facing JSON contract
                        -- emitted by AgentSessionEventInsertRow. Keep this projection in
                        -- lockstep with that DTO; EnsureInsertJsonContract fails fast if
                        -- optional JSONB fields such as arguments_json fall out of shape.
                        id text,
                        occurred_at_utc timestamp with time zone,
                        source_sequence bigint,
                        source_record_id text,
                        organization_id text,
                        conversation_id text,
                        event_type smallint,
                        prompt_group_id text,
                        tool_call_id text,
                        provider_request_id text,
                        prompt_text text,
                        prompt_length integer,
                        tool_name text,
                        tool_name_raw text,
                        mcp_server text,
                        mcp_server_origin text,
                        mcp_server_scope text,
                        arguments_json jsonb,
                        output_snippet text,
                        success boolean,
                        duration_ms integer,
                        decision text,
                        decision_source text,
                        model text,
                        input_tokens integer,
                        cached_tokens integer,
                        output_tokens integer,
                        reasoning_tokens integer,
                        tool_tokens integer,
                        cost_usd numeric,
                        cost_source smallint,
                        cost_units_raw bigint,
                        query_source text,
                        is_housekeeping boolean
                    )
                )
                INSERT INTO zeeq.agent_session_events (
                    id,
                    occurred_at_utc,
                    source_sequence,
                    source_record_id,
                    organization_id,
                    conversation_id,
                    event_type,
                    prompt_group_id,
                    tool_call_id,
                    provider_request_id,
                    prompt_text,
                    prompt_length,
                    tool_name,
                    tool_name_raw,
                    mcp_server,
                    mcp_server_origin,
                    mcp_server_scope,
                    arguments_json,
                    output_snippet,
                    success,
                    duration_ms,
                    decision,
                    decision_source,
                    model,
                    input_tokens,
                    cached_tokens,
                    output_tokens,
                    reasoning_tokens,
                    tool_tokens,
                    cost_usd,
                    cost_source,
                    cost_units_raw,
                    query_source,
                    is_housekeeping
                )
                SELECT
                    id,
                    occurred_at_utc,
                    source_sequence,
                    source_record_id,
                    organization_id,
                    conversation_id,
                    event_type,
                    prompt_group_id,
                    tool_call_id,
                    provider_request_id,
                    prompt_text,
                    prompt_length,
                    tool_name,
                    tool_name_raw,
                    mcp_server,
                    mcp_server_origin,
                    mcp_server_scope,
                    arguments_json,
                    output_snippet,
                    success,
                    duration_ms,
                    decision,
                    decision_source,
                    model,
                    input_tokens,
                    cached_tokens,
                    output_tokens,
                    reasoning_tokens,
                    tool_tokens,
                    cost_usd,
                    cost_source,
                    cost_units_raw,
                    query_source,
                    is_housekeeping
                FROM rows
                ON CONFLICT DO NOTHING
                RETURNING id AS "Value"
                """;

            var insertedIds = await db
                .Database.SqlQuery<string>(sql)
                .TagWithOperationCallSite("telemetry.agent_session_events.insert_new")
                .ToListAsync(cancellationToken);

            foreach (var insertedId in insertedIds)
            {
                if (!string.IsNullOrWhiteSpace(insertedId))
                {
                    inserted.Add(insertedId);
                }
            }
        }

        return inserted;
    }

    private static AgentSessionEventInsertRow ToInsertRow(AgentSessionEvent e) =>
        new(
            RemoveNulCharactersRequired(e.Id),
            e.OccurredAtUtc,
            e.SourceSequence,
            RemoveNulCharacters(e.SourceRecordId),
            RemoveNulCharactersRequired(e.OrganizationId),
            RemoveNulCharactersRequired(e.ConversationId),
            (byte)e.EventType,
            RemoveNulCharacters(e.PromptGroupId),
            RemoveNulCharacters(e.ToolCallId),
            RemoveNulCharacters(e.ProviderRequestId),
            RemoveNulCharacters(e.PromptText),
            e.PromptLength,
            RemoveNulCharacters(e.ToolName),
            RemoveNulCharacters(e.ToolNameRaw),
            RemoveNulCharacters(e.McpServer),
            RemoveNulCharacters(e.McpServerOrigin),
            RemoveNulCharacters(e.McpServerScope),
            CloneWithNulCharactersRemoved(e.ArgumentsJson),
            RemoveNulCharacters(e.OutputSnippet),
            e.Success,
            e.DurationMs,
            RemoveNulCharacters(e.Decision),
            RemoveNulCharacters(e.DecisionSource),
            RemoveNulCharacters(e.Model),
            e.InputTokens,
            e.CachedTokens,
            e.OutputTokens,
            e.ReasoningTokens,
            e.ToolTokens,
            e.CostUsd,
            e.CostSource is null ? null : (byte)e.CostSource,
            e.CostUnitsRaw,
            RemoveNulCharacters(e.QuerySource),
            e.IsHousekeeping
        );

    /// <summary>
    /// Removes NUL characters, which PostgreSQL cannot represent in text values.
    /// </summary>
    /// <remarks>
    /// Telemetry is supplied by external agent processes. The event batch is serialized to JSONB
    /// before insertion, and PostgreSQL rejects a JSON string containing <c>U+0000</c>. The
    /// fast path preserves the original string reference for the overwhelmingly common clean case.
    /// NOTE: Telemetry is time-based observability data, so malformed NUL characters are dropped
    /// even though exceptionally rare external identifiers or JSON keys could then collide.
    /// </remarks>
    private static string? RemoveNulCharacters(string? value) =>
        value is null || !value.Contains('\0') ? value : value.Replace("\0", string.Empty);

    private static string RemoveNulCharactersRequired(string value) =>
        value.Contains('\0') ? value.Replace("\0", string.Empty) : value;

    /// <summary>
    /// Clones JSON arguments after removing NUL characters from object keys and string values.
    /// </summary>
    /// <remarks>
    /// JSONB can contain arbitrary nested tool arguments, so normalizing only top-level event
    /// fields would still allow one malformed nested string or property name to reject the whole
    /// insert batch. NUL-bearing property names follow the same intentional collision policy as
    /// other telemetry strings.
    /// </remarks>
    /// <returns>
    /// <see langword="null"/> when arguments are absent; otherwise, an independently owned
    /// <see cref="JsonElement"/> safe to use after the source document is disposed.
    /// </returns>
    private static JsonElement? CloneWithNulCharactersRemoved(JsonDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        if (!ContainsNulCharacter(root))
        {
            return root.Clone();
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteWithoutNulCharacters(writer, root);
        }

        using var sanitized = JsonDocument.Parse(buffer.WrittenMemory);

        return sanitized.RootElement.Clone();
    }

    private static bool ContainsNulCharacter(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()?.Contains('\0') == true,
            JsonValueKind.Array => element.EnumerateArray().Any(ContainsNulCharacter),
            JsonValueKind.Object => element
                .EnumerateObject()
                .Any(property =>
                    property.Name.Contains('\0') || ContainsNulCharacter(property.Value)
                ),
            _ => false,
        };

    private static void WriteWithoutNulCharacters(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(RemoveNulCharactersRequired(property.Name));
                    WriteWithoutNulCharacters(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteWithoutNulCharacters(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RemoveNulCharacters(element.GetString()));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void EnsureInsertJsonContract(string rowsJson)
    {
        if (!rowsJson.Contains("\"arguments_json\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Agent session event insert JSON must include the arguments_json field expected by the SQL projection."
            );
        }
    }

    private static bool IsPullRequestSessionLinkDuplicate(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgresException
        && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
        && postgresException.ConstraintName
            == "ix_agent_pull_request_session_links_organization_id_pull_reque";

    private static string? CanonicalRepositoryOrNull(string? repositoryRemoteUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryRemoteUrl))
        {
            return null;
        }

        var repository = TelemetryRepositoryIdentity.Normalize(repositoryRemoteUrl);

        return repository.Length == 0 ? null : repository;
    }
}
