namespace Zeeq.Core.Llm;

/// <summary>
/// Names OpenTelemetry sources emitted by the shared LLM client pipeline.
/// </summary>
public static class LlmTelemetry
{
    /// <summary>
    /// Activity source used by Microsoft.Extensions.AI chat-client instrumentation.
    /// </summary>
    public const string ActivitySourceName = "Zeeq.Llm";
}
