namespace Zeeq.Core.Models;

/// <summary>
/// Read contract for the Sessions inbox: recent agent conversations and one
/// conversation's prompt/usage detail.
/// </summary>
/// <remarks>
/// Kept as an abstraction so the endpoint layer does not depend on
/// <c>PostgresDbContext</c> directly, mirroring <see cref="IAgentTelemetryDomainStore"/>.
/// Pricing math stays out of this store — <see cref="AgentCompletionModelAggregate"/> only
/// carries token/cost sums, because the pricing catalog needed to turn those into a cost
/// breakdown lives in <c>Zeeq.Platform.Telemetry</c>, which this project cannot depend on.
/// The store does aggregate at the SQL level (<c>GROUP BY</c> model), though, rather than
/// materializing every completion event into memory — see <see cref="GetDetailAsync"/>.
///
/// NOTE: <see cref="ListRecentAsync"/> is always caller-scoped (no "All" — every
/// conversation belongs to the person who ran it, unlike a PR anyone on the team might
/// review), but <see cref="GetDetailAsync"/> deliberately does not filter by ownership at
/// all: any org member with a direct link to a conversation can open it. That split is
/// intentional — it lets a conversation be shared by URL without exposing every other
/// member's conversations in the inbox listing.
/// </remarks>
public interface IAgentConversationQueryStore
{
    /// <summary>
    /// Lists the caller's recent conversations using a partition-aware seek cursor, newest first.
    /// </summary>
    Task<AgentConversationStreamPage> ListRecentAsync(
        AgentConversationStreamQuery query,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Loads one conversation's summary, prompt events, and per-model usage aggregates.
    /// Not ownership-scoped — see the remarks above.
    /// </summary>
    /// <returns><see langword="null"/> when no conversation matches.</returns>
    Task<AgentConversationDetail?> GetDetailAsync(
        string organizationId,
        string conversationId,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Cursor boundary for the newest-first conversation stream.
/// </summary>
/// <param name="StartedAtUtc">Conversation start timestamp used for ordering.</param>
/// <param name="Id">Stable conversation id used as the tie-breaker.</param>
public sealed record AgentConversationStreamCursor(DateTimeOffset StartedAtUtc, string Id);

/// <summary>
/// Filter for the Sessions inbox stream query. The inbox is always scoped to the caller —
/// there is no "All" option (see remarks on <see cref="IAgentConversationQueryStore"/>).
/// </summary>
/// <param name="OrganizationId">Owning organization; every query is single-organization.</param>
/// <param name="SubjectUserId">
/// Resolves ownership via <c>CreatedById</c> plus the subject's own email and active
/// email aliases.
/// </param>
/// <param name="Cursor">Seek boundary from a previous page; <see langword="null"/> starts from newest.</param>
/// <param name="PageSize">Requested page size, clamped to [1, 100] by the store.</param>
public sealed record AgentConversationStreamQuery(
    string OrganizationId,
    string SubjectUserId,
    AgentConversationStreamCursor? Cursor = null,
    int PageSize = 50
);

/// <summary>
/// Page of conversation summary rows with the next cursor boundary.
/// </summary>
/// <param name="Items">Rows for this page, newest first.</param>
/// <param name="NextCursor">
/// Seek boundary for the next page, or <see langword="null"/> when this page reached the
/// end of the stream (the store fetches one sentinel row past the page size to tell these
/// apart, so a full-but-final page does not return a cursor that leads nowhere).
/// </param>
public sealed record AgentConversationStreamPage(
    IReadOnlyList<AgentConversationSummary> Items,
    AgentConversationStreamCursor? NextCursor
);

/// <summary>
/// Conversation-level inbox row. Display-name resolution for
/// <see cref="OwnerEmail"/>/<see cref="CreatedById"/> happens client-side against the
/// already-loaded organization member list, so this projection stays raw.
/// </summary>
/// <param name="Id">Telemetry conversation id.</param>
/// <param name="Harness">Harness family — <c>claude-code</c>, <c>codex</c>, <c>copilot-chat</c>, etc.</param>
/// <param name="HarnessVariant">Codex <c>originator</c> or Claude <c>terminal.type</c>, when reported.</param>
/// <param name="RepoRemoteUrl">Canonical <c>owner/repo</c> identity, when the harness reported one.</param>
/// <param name="HeadBranch">Git branch active during the conversation, when reported.</param>
/// <param name="OwnerEmail">Harness-reported owner email; raw, not alias-resolved (see remarks above).</param>
/// <param name="CreatedById">Zeeq user id of the authenticated ingest principal, when trusted.</param>
/// <param name="StartedAtUtc">Earliest accepted event timestamp; also the pagination sort key.</param>
/// <param name="CompletedAtUtc"><see langword="null"/> while the conversation is still active.</param>
/// <param name="TotalInputTokens">
/// Rollup column on <c>agent_conversations</c>. NOTE: not reliably maintained by ingestion
/// today (observed as 0 even for conversations with real completion events) — prefer
/// <see cref="AgentCompletionModelAggregate"/> (from <see cref="AgentConversationDetail"/>)
/// when a live total is needed. This inbox row does not have that available; see the
/// deferred fix noted on <c>SessionInboxList.vue</c>'s row projection.
/// </param>
/// <param name="TotalOutputTokens">See <see cref="TotalInputTokens"/> caveat.</param>
/// <param name="TotalCostUsd">See <see cref="TotalInputTokens"/> caveat.</param>
public sealed record AgentConversationSummary(
    string Id,
    string Harness,
    string? HarnessVariant,
    string? RepoRemoteUrl,
    string? HeadBranch,
    string? OwnerEmail,
    string? CreatedById,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int TotalInputTokens,
    int TotalOutputTokens,
    decimal? TotalCostUsd
);

/// <summary>
/// One prompt event's timeline-relevant fields — the only event type with a message
/// payload from the user, so this is what the Sessions detail timeline renders.
/// </summary>
/// <param name="Id">Event id.</param>
/// <param name="OccurredAtUtc">Event timestamp; timeline ordering key.</param>
/// <param name="PromptGroupId">Correlates this prompt to its turn's tool calls/completions.</param>
/// <param name="PromptText">User prompt text, subject to the harness's obfuscation policy.</param>
/// <param name="PromptLength">Prompt text length, reported independent of truncated storage.</param>
/// <param name="TurnInputTokens">
/// Sum of input tokens across completion events chronologically between this prompt and the
/// next one (see the store's time-window correlation notes — <paramref name="PromptGroupId"/>
/// is reported inconsistently across harnesses, e.g. Codex and Pi never set it, so it isn't a
/// reliable join key). <see langword="null"/> when the turn has no completions yet (e.g. the
/// most recent prompt).
/// </param>
/// <param name="TurnOutputTokens">See <paramref name="TurnInputTokens"/>; output-token equivalent.</param>
public sealed record AgentConversationPromptEvent(
    string Id,
    DateTimeOffset OccurredAtUtc,
    string? PromptGroupId,
    string? PromptText,
    int? PromptLength,
    long? TurnInputTokens,
    long? TurnOutputTokens
);

/// <summary>
/// Raw completion-event fields needed to attach per-turn token sums onto a prompt (see
/// the store's <c>AttachTurnTokens</c>). Internal to the store — not part of
/// <see cref="AgentConversationDetail"/>; conversation-wide cost/token totals come from
/// <see cref="AgentCompletionModelAggregate"/> instead, which the store computes via a
/// SQL-side aggregate rather than materializing every completion event.
/// </summary>
/// <param name="Model">Model name reported by the harness; selects pricing rates downstream.</param>
/// <param name="InputTokens">Total input tokens, including <paramref name="CachedTokens"/> when present.</param>
/// <param name="CachedTokens">Cached-input subset of <paramref name="InputTokens"/>.</param>
/// <param name="OutputTokens">Output token count.</param>
/// <param name="ReasoningTokens">Reasoning token subset of <paramref name="OutputTokens"/> (Copilot only).</param>
/// <param name="ToolTokens">Tool token count (Codex only).</param>
/// <param name="CostUsd">Cost already computed at ingest time (reported or estimated); the authoritative total.</param>
/// <param name="OccurredAtUtc">
/// Event timestamp — used only to bucket this completion into its turn's
/// <see cref="AgentConversationPromptEvent"/> by time window.
/// </param>
public sealed record AgentCompletionEventForUsage(
    string? Model,
    int? InputTokens,
    int? CachedTokens,
    int? OutputTokens,
    int? ReasoningTokens,
    int? ToolTokens,
    decimal? CostUsd,
    DateTimeOffset OccurredAtUtc
);

/// <summary>
/// Per-model aggregate of a conversation's completion events, computed by a SQL-side
/// <c>GROUP BY</c> so the app never materializes the full completion history into memory
/// regardless of how long-running the conversation is. A conversation typically touches
/// only 1-3 distinct models, so this stays small even for a conversation with tens of
/// thousands of completion events.
/// </summary>
/// <param name="Model">Model name reported by the harness; selects pricing rates downstream.</param>
/// <param name="EventCount">Number of completion events for this model.</param>
/// <param name="SumInputTokens">Sum of input tokens (including cached), unclamped against any other field.</param>
/// <param name="SumCachedTokens">Sum of cached-input tokens, unclamped against <see cref="SumInputTokens"/>.</param>
/// <param name="SumOutputTokens">Sum of output tokens.</param>
/// <param name="SumReasoningTokens">Sum of reasoning tokens, unclamped against <see cref="SumOutputTokens"/>.</param>
/// <param name="SumToolTokens">Sum of tool tokens (Codex only).</param>
/// <param name="SumCostUsd">Sum of persisted cost across events that had one (0 contribution from events that didn't).</param>
/// <param name="EventsMissingCost">
/// Count of events with no persisted cost — the calculator uses this to tell "every event
/// priced, total is $0" from "some/all costs unknown, total is unknown".
/// </param>
/// <param name="MaxInputTokens">Largest single-event input token count for this model.</param>
/// <param name="MaxCachedTokens">Largest single-event cached-input token count for this model.</param>
public sealed record AgentCompletionModelAggregate(
    string? Model,
    int EventCount,
    long SumInputTokens,
    long SumCachedTokens,
    long SumOutputTokens,
    long SumReasoningTokens,
    long SumToolTokens,
    decimal SumCostUsd,
    int EventsMissingCost,
    int MaxInputTokens,
    int MaxCachedTokens
);

/// <summary>
/// One conversation's full detail: summary, prompt timeline, and per-model usage aggregates.
/// </summary>
/// <param name="Summary">Conversation metadata row.</param>
/// <param name="Prompts">
/// Prompt events, ascending by time. Capped to the newest 500 when the conversation has
/// more (see the store implementation) — older prompts are trimmed, not newer ones.
/// </param>
/// <param name="UsageAggregates">
/// Per-model completion aggregates for <c>AgentConversationTokenUsageCalculator</c> and the
/// distinct-models list — unbounded across the whole conversation (a SQL aggregate, not raw
/// rows), so totals are always authoritative regardless of the prompt cap above.
/// </param>
public sealed record AgentConversationDetail(
    AgentConversationSummary Summary,
    IReadOnlyList<AgentConversationPromptEvent> Prompts,
    IReadOnlyList<AgentCompletionModelAggregate> UsageAggregates
);
