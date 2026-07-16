using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Zeeq.Core.Llm;

/// <summary>
/// Runs sanitized, bounded provider access tests for resolved LLM credentials.
/// </summary>
public sealed partial class LlmProviderAccessTester(
    ILlmClientFactory clientFactory,
    LlmProviderAccessTestOptions options,
    ILogger<LlmProviderAccessTester> log
) : ILlmProviderAccessTester
{
    /// <inheritdoc />
    public async Task<LlmProviderAccessTestResult> TestAsync(
        ResolvedLlmConfiguration configuration,
        string? prompt,
        CancellationToken cancellationToken
    )
    {
        var boundedPrompt = string.IsNullOrWhiteSpace(prompt)
            ? options.DefaultPrompt
            : prompt.Trim();

        if (boundedPrompt.Length > options.MaxPromptLength)
        {
            return Failure(
                configuration,
                latencyMs: 0,
                errorCode: "invalid_prompt",
                message: $"Prompt must be {options.MaxPromptLength} characters or fewer."
            );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = clientFactory.CreateChatClient(configuration);
            _ = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, boundedPrompt)],
                new ChatOptions { MaxOutputTokens = options.MaxOutputTokens, Temperature = 0 },
                timeout.Token
            );

            stopwatch.Stop();
            LogProviderAccessTestSucceeded(
                configuration.Provider,
                configuration.Model,
                configuration.Endpoint,
                stopwatch.ElapsedMilliseconds
            );

            return new LlmProviderAccessTestResult(
                Success: true,
                Provider: configuration.Provider,
                Model: configuration.Model,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: null,
                Message: "Provider access test completed."
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            return Failure(
                configuration,
                (int)stopwatch.ElapsedMilliseconds,
                errorCode: "timeout",
                message: "Provider access test timed out."
            );
        }
        catch (NotSupportedException ex)
        {
            stopwatch.Stop();
            LogProviderAccessTestFailed(
                ex,
                configuration.Provider,
                configuration.Model,
                configuration.Endpoint,
                stopwatch.ElapsedMilliseconds
            );

            return Failure(
                configuration,
                (int)stopwatch.ElapsedMilliseconds,
                errorCode: "unsupported_provider",
                message: "Provider is not supported by the current LLM client factory."
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogProviderAccessTestFailed(
                ex,
                configuration.Provider,
                configuration.Model,
                configuration.Endpoint,
                stopwatch.ElapsedMilliseconds
            );

            return Failure(
                configuration,
                (int)stopwatch.ElapsedMilliseconds,
                errorCode: "provider_error",
                message: "Provider access test failed. Check server logs for details."
            );
        }
    }

    private static LlmProviderAccessTestResult Failure(
        ResolvedLlmConfiguration configuration,
        int latencyMs,
        string errorCode,
        string message
    ) =>
        new(
            Success: false,
            Provider: configuration.Provider,
            Model: configuration.Model,
            LatencyMs: latencyMs,
            ErrorCode: errorCode,
            Message: message
        );

    [LoggerMessage(
        EventId = 13000,
        Level = LogLevel.Information,
        Message = "LLM provider access test succeeded. Provider={Provider}; Model={Model}; Endpoint={Endpoint}; LatencyMs={LatencyMs}"
    )]
    private partial void LogProviderAccessTestSucceeded(
        string provider,
        string model,
        string? endpoint,
        long latencyMs
    );

    [LoggerMessage(
        EventId = 13001,
        Level = LogLevel.Warning,
        Message = "LLM provider access test failed. Provider={Provider}; Model={Model}; Endpoint={Endpoint}; LatencyMs={LatencyMs}"
    )]
    private partial void LogProviderAccessTestFailed(
        Exception exception,
        string provider,
        string model,
        string? endpoint,
        long latencyMs
    );
}
