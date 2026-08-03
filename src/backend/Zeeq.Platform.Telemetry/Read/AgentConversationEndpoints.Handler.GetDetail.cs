using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Models;

namespace Zeeq.Platform.Telemetry.Read;

/// <summary>
/// Handles the single-conversation detail endpoint.
/// </summary>
public sealed class GetAgentConversationDetailHandler(IAgentConversationQueryStore conversations)
    : IEndpointHandler
{
    /// <summary>
    /// Loads one conversation's summary, prompt timeline, and token usage summary.
    /// </summary>
    public async Task<Results<NotFound, Ok<AgentConversationDetailResponse>>> HandleAsync(
        string organizationId,
        string conversationId,
        CancellationToken cancellationToken
    )
    {
        var detail = await conversations.GetDetailAsync(
            organizationId,
            conversationId,
            cancellationToken
        );

        if (detail is null)
        {
            return TypedResults.NotFound();
        }

        // Null only when the conversation has no completion events yet (e.g. mid-turn).
        var usage = AgentConversationTokenUsageCalculator.Summarize(detail.UsageAggregates);
        var models = detail
            .UsageAggregates.Where(a => !string.IsNullOrWhiteSpace(a.Model))
            .Select(a => a.Model!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(model => model, StringComparer.Ordinal)
            .ToArray();

        return TypedResults.Ok(
            new AgentConversationDetailResponse(
                AgentConversationEndpointMapping.ToDto(detail.Summary),
                detail.Prompts.Select(AgentConversationEndpointMapping.ToDto).ToArray(),
                usage is null ? null : AgentConversationEndpointMapping.ToDto(usage),
                models
            )
        );
    }
}
