namespace Zeeq.Core.Llm;

/// <summary>
/// Tests whether a resolved LLM provider/model/key tuple can complete a bounded chat call.
/// </summary>
public interface ILlmProviderAccessTester
{
    /// <summary>
    /// Runs a bounded provider access test and returns sanitized status metadata.
    /// </summary>
    Task<LlmProviderAccessTestResult> TestAsync(
        ResolvedLlmConfiguration configuration,
        string? prompt,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Sanitized result for an LLM provider access test.
/// </summary>
public sealed record LlmProviderAccessTestResult(
    bool Success,
    string Provider,
    string Model,
    int LatencyMs,
    string? ErrorCode,
    string Message
);

/// <summary>
/// Runtime bounds for provider access tests.
/// </summary>
public sealed record LlmProviderAccessTestOptions
{
    /// <summary>
    /// Default test prompt used when callers do not provide one.
    /// </summary>
    public string DefaultPrompt { get; init; } = "Reply with OK.";

    /// <summary>
    /// Maximum prompt length accepted by the tester.
    /// </summary>
    public int MaxPromptLength { get; init; } = 500;

    /// <summary>
    /// Maximum generated tokens requested from the provider.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 16;

    /// <summary>
    /// Timeout applied to the provider call.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}
