using System.Diagnostics;
using System.Diagnostics.Metrics;
using Zeeq.Core.Common;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Processing;

/// <summary>
/// Emits durable business metrics for normalized agent telemetry.
/// </summary>
/// <remarks>
/// The metrics pipeline persists measurements from <see cref="ZeeqTelemetry.Metrics"/>
/// only when the measurement carries an <c>organization_id</c> tag. Use the existing
/// promoted tag names here: <c>user</c> becomes the stored <c>user_email</c> column,
/// and <c>tool_name</c> becomes the stored tool column. Harness-specific dimensions
/// stay in the metric event's JSON tag bag.
/// </remarks>
public static class AgentTelemetryMetrics
{
    /// <summary>Counts newly created agent conversations.</summary>
    public const string SessionCounterName = "zeeq_agent_session_counter";

    /// <summary>Counts non-housekeeping user prompts.</summary>
    public const string PromptCounterName = "zeeq_agent_prompt_counter";

    /// <summary>Counts non-housekeeping tool execution results.</summary>
    public const string ToolCallCounterName = "zeeq_agent_tool_call_counter";

    /// <summary>Records non-housekeeping completion token counts by token kind.</summary>
    public const string TokenUsageHistogramName = "zeeq_agent_token_usage";

    /// <summary>Records non-housekeeping completion costs in USD.</summary>
    public const string CostUsdHistogramName = "zeeq_agent_cost_usd";

    /// <summary>Records non-housekeeping completion costs in provider-native raw billing units.</summary>
    public const string CostUnitsRawHistogramName = "zeeq_agent_cost_units_raw";

    /// <summary>Counts newly created pull-request to conversation links.</summary>
    public const string PullRequestLinkCounterName = "zeeq_agent_pr_link_counter";

    private static readonly Counter<int> SessionCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(SessionCounterName);

    private static readonly Counter<int> PromptCounter = ZeeqTelemetry.Metrics.CreateCounter<int>(
        PromptCounterName
    );

    private static readonly Counter<int> ToolCallCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(ToolCallCounterName);

    private static readonly Histogram<int> TokenUsageHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<int>(TokenUsageHistogramName);

    private static readonly Histogram<double> CostUsdHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<double>(CostUsdHistogramName);

    private static readonly Histogram<long> CostUnitsRawHistogram =
        ZeeqTelemetry.Metrics.CreateHistogram<long>(CostUnitsRawHistogramName);

    private static readonly Counter<int> PullRequestLinkCounter =
        ZeeqTelemetry.Metrics.CreateCounter<int>(PullRequestLinkCounterName);

    /// <summary>
    /// Emits one session metric for a conversation that was inserted by the domain store.
    /// </summary>
    /// <param name="conversation">The newly created conversation.</param>
    public static void RecordNewSession(AgentConversation conversation)
    {
        SessionCounter.Add(1, ConversationTags(conversation));
    }

    /// <summary>
    /// Emits metrics for non-housekeeping session events after their domain write succeeds.
    /// </summary>
    /// <param name="conversation">Conversation context used for organization, harness, and user tags.</param>
    /// <param name="sessionEvent">The event persisted for the conversation.</param>
    public static void RecordEvent(AgentConversation conversation, AgentSessionEvent sessionEvent)
    {
        if (sessionEvent.IsHousekeeping)
        {
            return;
        }

        switch (sessionEvent.EventType)
        {
            case AgentSessionEventType.Prompt:
                PromptCounter.Add(1, ConversationTags(conversation));
                break;

            case AgentSessionEventType.ToolResult:
                var toolTags = ConversationTags(conversation);
                toolTags.Add("tool_name", sessionEvent.ToolName);
                toolTags.Add("mcp_server", sessionEvent.McpServer);
                toolTags.Add("success", sessionEvent.Success);

                ToolCallCounter.Add(1, toolTags);
                break;

            case AgentSessionEventType.Completion:
                RecordCompletion(conversation, sessionEvent);
                break;
        }
    }

    /// <summary>
    /// Emits one link metric after a pull-request link is newly persisted.
    /// </summary>
    /// <param name="organizationId">Owning organization for the link.</param>
    /// <param name="harness">Harness family of the linked conversation.</param>
    /// <param name="linkOrigin">How the link was created.</param>
    public static void RecordPullRequestLink(
        string organizationId,
        string? harness,
        AgentSessionLinkOrigin linkOrigin
    )
    {
        var tags = new TagList
        {
            { "organization_id", organizationId },
            { "harness", harness },
            { "link_origin", linkOrigin.ToString() },
        };

        PullRequestLinkCounter.Add(1, tags);
    }

    private static void RecordCompletion(
        AgentConversation conversation,
        AgentSessionEvent sessionEvent
    )
    {
        RecordTokens(conversation, sessionEvent, sessionEvent.InputTokens, "input");
        RecordTokens(conversation, sessionEvent, sessionEvent.OutputTokens, "output");
        RecordTokens(conversation, sessionEvent, sessionEvent.CachedTokens, "cached");
        RecordTokens(conversation, sessionEvent, sessionEvent.ReasoningTokens, "reasoning");
        RecordTokens(conversation, sessionEvent, sessionEvent.ToolTokens, "tool");

        if (sessionEvent.CostUsd is not null)
        {
            var tags = CompletionTags(conversation, sessionEvent);
            tags.Add("cost_source", sessionEvent.CostSource?.ToString());

            CostUsdHistogram.Record(decimal.ToDouble(sessionEvent.CostUsd.Value), tags);
        }

        if (sessionEvent.CostUnitsRaw is not null)
        {
            var tags = CompletionTags(conversation, sessionEvent);
            tags.Add("cost_source", sessionEvent.CostSource?.ToString());

            CostUnitsRawHistogram.Record(sessionEvent.CostUnitsRaw.Value, tags);
        }
    }

    private static void RecordTokens(
        AgentConversation conversation,
        AgentSessionEvent sessionEvent,
        int? tokens,
        string tokenKind
    )
    {
        if (tokens is null)
        {
            return;
        }

        var tags = CompletionTags(conversation, sessionEvent);
        tags.Add("token_kind", tokenKind);

        TokenUsageHistogram.Record(tokens.Value, tags);
    }

    private static TagList ConversationTags(AgentConversation conversation) =>
        new()
        {
            { "organization_id", conversation.OrganizationId },
            { "user", UserTagValue(conversation) },
            { "harness", conversation.Harness },
        };

    private static TagList CompletionTags(
        AgentConversation conversation,
        AgentSessionEvent sessionEvent
    )
    {
        var tags = ConversationTags(conversation);
        tags.Add("model", sessionEvent.Model);

        return tags;
    }

    private static string? UserTagValue(AgentConversation conversation) =>
        !string.IsNullOrWhiteSpace(conversation.OwnerEmail)
            ? conversation.OwnerEmail
            : conversation.CreatedById;
}
