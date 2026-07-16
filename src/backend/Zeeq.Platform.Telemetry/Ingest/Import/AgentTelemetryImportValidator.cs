namespace Zeeq.Platform.Telemetry.Ingest.Import;

/// <summary>
/// Validates direct JSON telemetry import requests before they are mapped to OTLP.
/// </summary>
public sealed class AgentTelemetryImportValidator
{
    private const int MaxEvents = 1_000;
    private const int MaxStringLength = 16 * 1024;

    /// <summary>
    /// Returns validation errors keyed by request field. An empty dictionary means
    /// the request can be mapped and sent through the shared ingest path.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> Validate(AgentTelemetryImportRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            errors["conversation_id"] = ["conversation_id is required."];
        }

        if (string.IsNullOrWhiteSpace(request.HarnessName))
        {
            errors["harness_name"] = ["harness_name is required."];
        }

        if (request.Events is not { Count: > 0 })
        {
            errors["events"] = ["At least one event is required."];
        }
        else if (request.Events.Count > MaxEvents)
        {
            errors["events"] = [$"At most {MaxEvents} events can be imported in one request."];
        }
        else
        {
            ValidateEvents(request.Events, errors);
        }

        ValidateMaxLength("conversation_id", request.ConversationId, errors);
        ValidateMaxLength("harness_name", request.HarnessName, errors);
        ValidateMaxLength("harness_version", request.HarnessVersion, errors);
        ValidateMaxLength("repository_remote_url", request.RepositoryRemoteUrl, errors);
        ValidateMaxLength("head_branch", request.HeadBranch, errors);
        ValidateMaxLength("head_sha", request.HeadSha, errors);

        return errors;
    }

    private static void ValidateEvents(
        IReadOnlyList<ImportedAgentEvent> events,
        Dictionary<string, string[]> errors
    )
    {
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            var prefix = $"events[{i}]";

            if (!Enum.IsDefined(item.Kind))
            {
                errors[$"{prefix}.kind"] = ["kind must be prompt, toolResult, or completion."];
            }

            ValidateMaxLength($"{prefix}.event.id", item.EventId, errors);
            ValidateMaxLength($"{prefix}.prompt_text", item.PromptText, errors);
            ValidateMaxLength($"{prefix}.gen_ai.tool.name", item.ToolName, errors);
            ValidateMaxLength($"{prefix}.gen_ai.tool.call.id", item.ToolCallId, errors);
            ValidateMaxLength($"{prefix}.mcp.server.name", item.McpServerName, errors);
            ValidateMaxLength($"{prefix}.gen_ai.tool.call.result", item.ToolResult, errors);
            ValidateMaxLength($"{prefix}.gen_ai.request.model", item.Model, errors);
            ValidateMaxLength($"{prefix}.user.email", item.UserEmail, errors);
            ValidateMaxLength($"{prefix}.organization_id", item.OrganizationId, errors);

            ValidateNonNegative($"{prefix}.prompt_length", item.PromptLength, errors);
            ValidateNonNegative(
                $"{prefix}.gen_ai.tool.call.duration_ms",
                item.ToolDurationMs,
                errors
            );
            ValidateNonNegative($"{prefix}.gen_ai.usage.input_tokens", item.InputTokens, errors);
            ValidateNonNegative($"{prefix}.gen_ai.usage.cached_tokens", item.CachedTokens, errors);
            ValidateNonNegative($"{prefix}.gen_ai.usage.output_tokens", item.OutputTokens, errors);
            ValidateNonNegative(
                $"{prefix}.gen_ai.usage.reasoning_tokens",
                item.ReasoningTokens,
                errors
            );

            if (item.CostUsd is < 0)
            {
                errors[$"{prefix}.zeeq.cost.usd"] = ["zeeq.cost.usd must not be negative."];
            }
        }
    }

    private static void ValidateMaxLength(
        string field,
        string? value,
        Dictionary<string, string[]> errors
    )
    {
        if (value is { Length: > MaxStringLength })
        {
            errors[field] = [$"{field} must be {MaxStringLength} characters or fewer."];
        }
    }

    private static void ValidateNonNegative(
        string field,
        int? value,
        Dictionary<string, string[]> errors
    )
    {
        if (value is < 0)
        {
            errors[field] = [$"{field} must not be negative."];
        }
    }
}
