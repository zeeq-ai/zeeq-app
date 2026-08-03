namespace Zeeq.Data.Postgres.Telemetry;

/// <summary>
/// Inserted event row returned by the partitioned event-table insert statement.
/// </summary>
/// <remarks>
/// This is the normalized database row shape after NUL stripping and
/// <c>ON CONFLICT DO NOTHING</c> idempotency have both run. The inline rollup code uses this
/// projection, rather than the original in-memory events, so only genuinely persisted events
/// contribute to title, token, and cost deltas.
/// </remarks>
internal sealed class InsertedAgentSessionEventRow
{
    /// <summary>Persisted event id.</summary>
    public required string Id { get; init; }

    /// <summary>Persisted event timestamp and partition key.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>Harness source sequence used as a title tie-breaker when present.</summary>
    public long? SourceSequence { get; init; }

    /// <summary>Organization that owns the inserted event.</summary>
    public required string OrganizationId { get; init; }

    /// <summary>Conversation that owns the inserted event.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Persisted event type discriminator.</summary>
    public byte EventType { get; init; }

    /// <summary>Prompt text used only for non-empty, set-once title candidate selection.</summary>
    public string? PromptText { get; init; }

    /// <summary>Completion input tokens included in the inline rollup.</summary>
    public int? InputTokens { get; init; }

    /// <summary>Completion output tokens included in the inline rollup.</summary>
    public int? OutputTokens { get; init; }

    /// <summary>Persisted completion cost; null means the conversation total cost is unknown.</summary>
    public decimal? CostUsd { get; init; }

    /// <summary>Whether this event is excluded from title selection.</summary>
    public bool IsHousekeeping { get; init; }
}
