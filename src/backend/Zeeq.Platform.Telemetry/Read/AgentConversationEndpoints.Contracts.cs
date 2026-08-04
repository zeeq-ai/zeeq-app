using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Read;

/// <summary>
/// Error response for Sessions API validation failures.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable description of the validation failure.</param>
public sealed record AgentConversationEndpointError(string Code, string Message);

/// <summary>
/// DTO cursor for the conversation stream, ordered newest first by
/// <c>StartedAtUtc, Id</c>.
/// </summary>
/// <param name="StartedAtUtc">Conversation start timestamp for the last row in the page.</param>
/// <param name="Id">Stable conversation id used as the cursor tie-breaker.</param>
public sealed record AgentConversationStreamCursorDto(DateTimeOffset StartedAtUtc, string Id);

/// <summary>
/// Conversation row returned to the Sessions inbox. <c>OwnerEmail</c>/<c>CreatedById</c> are
/// raw — the client resolves a display name against the organization member list it already
/// loads, rather than the server joining one here.
/// </summary>
/// <param name="Id">Telemetry conversation id; use to fetch detail or deep-link to <c>/sessions/{id}</c>.</param>
/// <param name="Harness">Harness family — <c>claude-code</c>, <c>codex</c>, <c>copilot-chat</c>, etc.</param>
/// <param name="HarnessVariant">Codex <c>originator</c> or Claude <c>terminal.type</c>, when reported.</param>
/// <param name="RepoRemoteUrl">Canonical <c>owner/repo</c> identity, when the harness reported one.</param>
/// <param name="HeadBranch">Git branch active during the conversation, when reported.</param>
/// <param name="OwnerEmail">Harness-reported owner email, raw (not alias-resolved).</param>
/// <param name="CreatedById">Zeeq user id of the authenticated ingest principal, when trusted.</param>
/// <param name="StartedAtUtc">Earliest accepted event timestamp.</param>
/// <param name="CompletedAtUtc"><see langword="null"/> while the conversation is still active.</param>
/// <param name="TotalInputTokens">
/// NOTE: not reliably populated by ingestion today — often 0 even for conversations with real
/// completion events. Prefer <see cref="AgentConversationTokenUsageDto"/> from the detail
/// endpoint for an accurate live total.
/// </param>
/// <param name="TotalOutputTokens">See <see cref="TotalInputTokens"/> caveat.</param>
/// <param name="TotalCostUsd">See <see cref="TotalInputTokens"/> caveat.</param>
public sealed record AgentConversationListItemDto(
    string Id,
    string Harness,
    string? HarnessVariant,
    string? RepoRemoteUrl,
    string? HeadBranch,
    string? OwnerEmail,
    string? CreatedById,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal? TotalCostUsd
);

/// <summary>
/// Cursor-paginated page of Sessions inbox rows.
/// </summary>
/// <param name="Items">Rows for this page, newest first.</param>
/// <param name="NextCursor">Pass back as <c>cursorStartedAtUtc</c>/<c>cursorId</c> to fetch the next page; <see langword="null"/> at the end of the stream.</param>
public sealed record AgentConversationListResponse(
    IReadOnlyList<AgentConversationListItemDto> Items,
    AgentConversationStreamCursorDto? NextCursor
);

/// <summary>
/// One prompt event rendered in the conversation timeline — the only event type with a
/// message payload from the user.
/// </summary>
/// <param name="Id">Event id.</param>
/// <param name="OccurredAtUtc">Event timestamp; timeline ordering key.</param>
/// <param name="PromptGroupId">Correlates this prompt to its turn's tool calls/completions.</param>
/// <param name="PromptText">User prompt text, subject to the harness's obfuscation policy.</param>
/// <param name="PromptLength">Prompt text length, reported independent of truncated storage.</param>
/// <param name="InputTokens">Sum of input tokens across this turn's completions; <see langword="null"/> if none yet.</param>
/// <param name="OutputTokens">Sum of output tokens across this turn's completions; <see langword="null"/> if none yet.</param>
public sealed record AgentConversationPromptEventDto(
    string Id,
    DateTimeOffset OccurredAtUtc,
    string? PromptGroupId,
    string? PromptText,
    int? PromptLength,
    long? InputTokens,
    long? OutputTokens
);

/// <summary>
/// Token usage and cost summary for one conversation. See
/// <see cref="AgentConversationTokenUsageSummary"/> for field derivation.
/// </summary>
/// <param name="CompletionEventCount">Number of completion events summarized.</param>
/// <param name="PeakInputTokens">Largest single-event input token count observed.</param>
/// <param name="PeakCachedInputTokens">Largest single-event cached-input token count observed.</param>
/// <param name="BilledFreshInputTokens">Sum of non-cached input tokens across all events.</param>
/// <param name="BilledCachedInputTokens">Sum of cached input tokens across all events.</param>
/// <param name="BilledInputTokens">Sum of total input tokens (fresh + cached) across all events.</param>
/// <param name="BilledOutputTokens">Sum of output tokens across all events.</param>
/// <param name="BilledReasoningTokens">Sum of reasoning tokens (subset of output; Copilot only).</param>
/// <param name="BilledToolTokens">Sum of tool tokens (Codex only).</param>
/// <param name="CacheHitRate">Cached input tokens as a share of total input tokens.</param>
/// <param name="ReasoningShareOfOutput">Reasoning tokens as a share of total output tokens.</param>
/// <param name="TotalCostUsd">Authoritative total — sum of each event's already-persisted cost.</param>
/// <param name="AverageCostPerEventUsd"><paramref name="TotalCostUsd"/> divided by <paramref name="CompletionEventCount"/>.</param>
/// <param name="FreshInputCostUsd">
/// Independent catalog-rate estimate for fresh input tokens. NOT a component of
/// <paramref name="TotalCostUsd"/> — the two can (and often do) disagree, since
/// <paramref name="TotalCostUsd"/> is whatever cost was actually persisted per event
/// (reported or estimated at ingest by a possibly different rate source), while this is
/// "what it would cost at today's catalog rates." Do not render these as if they sum to
/// <paramref name="TotalCostUsd"/>.
/// </param>
/// <param name="CachedInputCostUsd">Independent catalog-rate estimate for cached input tokens; see <paramref name="FreshInputCostUsd"/>.</param>
/// <param name="OutputCostUsd">Independent catalog-rate estimate for output tokens; see <paramref name="FreshInputCostUsd"/>.</param>
/// <param name="CacheSavingsUsd">Estimated USD saved versus pricing all input tokens at the fresh rate.</param>
public sealed record AgentConversationTokenUsageDto(
    int CompletionEventCount,
    int PeakInputTokens,
    int PeakCachedInputTokens,
    long BilledFreshInputTokens,
    long BilledCachedInputTokens,
    long BilledInputTokens,
    long BilledOutputTokens,
    long BilledReasoningTokens,
    long BilledToolTokens,
    decimal? CacheHitRate,
    decimal? ReasoningShareOfOutput,
    decimal? TotalCostUsd,
    decimal? AverageCostPerEventUsd,
    decimal? FreshInputCostUsd,
    decimal? CachedInputCostUsd,
    decimal? OutputCostUsd,
    decimal? CacheSavingsUsd
);

/// <summary>
/// Full detail for one conversation: summary, prompt timeline, and token usage.
/// </summary>
/// <param name="Summary">Conversation metadata row, identical shape to an inbox list item.</param>
/// <param name="Prompts">Prompt events, ascending by time, capped at 500.</param>
/// <param name="TokenUsage"><see langword="null"/> when the conversation has no completion events yet.</param>
/// <param name="Models">
/// Distinct, non-blank model names seen across this conversation's completion events (a
/// conversation can span more than one, e.g. a cheaper model for housekeeping calls and a
/// different one for the actual turns), sorted alphabetically.
/// </param>
public sealed record AgentConversationDetailResponse(
    AgentConversationListItemDto Summary,
    IReadOnlyList<AgentConversationPromptEventDto> Prompts,
    AgentConversationTokenUsageDto? TokenUsage,
    IReadOnlyList<string> Models
);

/// <summary>
/// Maps Sessions domain records to their API DTOs.
/// </summary>
internal static class AgentConversationEndpointMapping
{
    /// <summary>Maps a conversation summary to its API representation.</summary>
    public static AgentConversationListItemDto ToDto(AgentConversationSummary summary) =>
        new(
            summary.Id,
            summary.Harness,
            summary.HarnessVariant,
            summary.RepoRemoteUrl,
            summary.HeadBranch,
            summary.OwnerEmail,
            summary.CreatedById,
            summary.StartedAtUtc,
            summary.CompletedAtUtc,
            summary.TotalInputTokens,
            summary.TotalOutputTokens,
            summary.TotalCostUsd
        );

    /// <summary>Maps a stream cursor to its API representation.</summary>
    public static AgentConversationStreamCursorDto? ToDto(AgentConversationStreamCursor? cursor) =>
        cursor is null ? null : new(cursor.StartedAtUtc, cursor.Id);

    /// <summary>Maps a prompt event to its API representation.</summary>
    public static AgentConversationPromptEventDto ToDto(AgentConversationPromptEvent prompt) =>
        new(
            prompt.Id,
            prompt.OccurredAtUtc,
            prompt.PromptGroupId,
            prompt.PromptText,
            prompt.PromptLength,
            prompt.TurnInputTokens,
            prompt.TurnOutputTokens
        );

    /// <summary>Maps a token usage summary to its API representation.</summary>
    public static AgentConversationTokenUsageDto ToDto(AgentConversationTokenUsageSummary usage) =>
        new(
            usage.CompletionEventCount,
            usage.PeakInputTokens,
            usage.PeakCachedInputTokens,
            usage.BilledFreshInputTokens,
            usage.BilledCachedInputTokens,
            usage.BilledInputTokens,
            usage.BilledOutputTokens,
            usage.BilledReasoningTokens,
            usage.BilledToolTokens,
            usage.CacheHitRate,
            usage.ReasoningShareOfOutput,
            usage.TotalCostUsd,
            usage.AverageCostPerEventUsd,
            usage.FreshInputCostUsd,
            usage.CachedInputCostUsd,
            usage.OutputCostUsd,
            usage.CacheSavingsUsd
        );

    /// <summary>Builds a domain stream cursor from the list endpoint's query-string cursor fields.</summary>
    public static AgentConversationStreamCursor? ToStreamCursor(
        DateTimeOffset? startedAtUtc,
        string? id
    ) => startedAtUtc is null || string.IsNullOrEmpty(id) ? null : new(startedAtUtc.Value, id);
}
