using System.Diagnostics;
using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Llm;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Runtime.Server.Diagnostics;

/// <summary>
/// Development-only endpoints for manually smoke testing app-level LLM clients.
/// </summary>
/// <remarks>
/// These endpoints are intentionally anonymous so they can be exercised from
/// curl while debugging local user-secret configuration. They are mapped only
/// when <c>IHostEnvironment.IsDevelopment()</c> is true, so production hosts do
/// not expose the route.
/// </remarks>
public sealed class LlmDiagnosticEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var environment = app.ServiceProvider.GetRequiredService<IHostEnvironment>();

        if (!environment.IsDevelopment())
        {
            return;
        }

        app.MapPost(
                "diagnostics/llm/fast-smoke-test",
                static (
                    [FromQuery] string? prompt,
                    [FromServices] RunFastLlmDiagnosticHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(prompt, ct)
            )
            .AllowAnonymous()
            .WithName("RunFastLlmDiagnostic")
            .WithTags("Diagnostics")
            .WithSummary("Smoke-test the Fast LLM client.")
            .WithDescription(
                """
                Sends a short, bounded prompt to the configured default **Fast** LLM client and
                returns sanitized result metadata (provider, model, latency, outcome). Use it to
                confirm local LLM credentials are wired up correctly. Pass an optional `prompt`
                query value to override the default.

                Development-only and anonymous so it can be exercised with `curl`: the route is
                mapped solely when `IHostEnvironment.IsDevelopment()` is true and is absent on
                production hosts.
                """
            );

        app.MapPost(
                "diagnostics/llm/embeddings-smoke-test",
                static (
                    [FromQuery] string? text,
                    [FromServices] RunEmbeddingLlmDiagnosticHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(text, ct)
            )
            .AllowAnonymous()
            .WithName("RunEmbeddingLlmDiagnostic")
            .WithTags("Diagnostics")
            .WithSummary("Smoke-test the snippet embedding client.")
            .WithDescription(
                """
                Sends a short, bounded text to the **Query**-profile snippet embedding client
                (`AppSettings:Llm:Embeddings`) and returns sanitized result metadata (model,
                dimensions, latency, outcome). Use it to confirm the embeddings API key is wired
                up correctly. Pass an optional `text` query value to override the default probe
                string. Returns `success: false` with `errorCode: "disabled"` when
                `AppSettings:Llm:Embeddings:Enabled` is false, rather than attempting a call.

                Development-only and anonymous so it can be exercised with `curl`: the route is
                mapped solely when `IHostEnvironment.IsDevelopment()` is true and is absent on
                production hosts.
                """
            );
    }
}

/// <summary>
/// Calls the configured default Fast LLM client for local smoke testing.
/// </summary>
public sealed class RunFastLlmDiagnosticHandler(
    DefaultLlmChatClients clients,
    LlmSettings settings,
    IHostEnvironment environment,
    ILogger<RunFastLlmDiagnosticHandler> log
) : IEndpointHandler
{
    private const string DefaultPrompt = "Reply with OK.";
    private const int MaxPromptLength = 500;

    /// <summary>
    /// Runs a bounded local Fast-client smoke test.
    /// </summary>
    public async Task<Results<Ok<LlmDiagnosticResponse>, BadRequest<string>, NotFound>> HandleAsync(
        string? prompt,
        CancellationToken cancellationToken
    )
    {
        if (!environment.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        var boundedPrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultPrompt : prompt.Trim();

        if (boundedPrompt.Length > MaxPromptLength)
        {
            return TypedResults.BadRequest(
                $"Prompt must be {MaxPromptLength} characters or fewer."
            );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await clients.Fast.GetResponseAsync(
                [new ChatMessage(ChatRole.User, boundedPrompt)],
                new ChatOptions { MaxOutputTokens = 16, Temperature = 0 },
                timeout.Token
            );

            stopwatch.Stop();

            log.LogInformation(
                "LLM Fast diagnostic succeeded. Provider={Provider}; Model={Model}; LatencyMs={LatencyMs}",
                settings.Models.Fast.Provider,
                settings.Models.Fast.Model,
                stopwatch.ElapsedMilliseconds
            );

            return TypedResults.Ok(
                new LlmDiagnosticResponse(
                    Success: true,
                    Provider: settings.Models.Fast.Provider,
                    Model: settings.Models.Fast.Model,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: null,
                    Message: NormalizeSmokeText(response.Text)
                )
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            return TypedResults.Ok(
                new LlmDiagnosticResponse(
                    Success: false,
                    Provider: settings.Models.Fast.Provider,
                    Model: settings.Models.Fast.Model,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: "timeout",
                    Message: "The Fast LLM diagnostic timed out."
                )
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            log.LogWarning(
                ex,
                "LLM Fast diagnostic failed. Provider={Provider}; Model={Model}; LatencyMs={LatencyMs}",
                settings.Models.Fast.Provider,
                settings.Models.Fast.Model,
                stopwatch.ElapsedMilliseconds
            );

            return TypedResults.Ok(
                new LlmDiagnosticResponse(
                    Success: false,
                    Provider: settings.Models.Fast.Provider,
                    Model: settings.Models.Fast.Model,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: "provider_error",
                    Message: "The Fast LLM diagnostic failed. Check zeeq-server logs for details."
                )
            );
        }
    }

    private static string NormalizeSmokeText(string text)
    {
        return text.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase) ? "OK" : "completed";
    }
}

/// <summary>
/// Sanitized response for the local Fast LLM diagnostic endpoint.
/// </summary>
public sealed record LlmDiagnosticResponse(
    bool Success,
    string Provider,
    string Model,
    int LatencyMs,
    string? ErrorCode,
    string Message
);

/// <summary>
/// Calls the Query-profile snippet embedding client for local smoke testing.
/// </summary>
/// <remarks>
/// Uses the <c>Query</c> profile (near-zero SDK retries, short timeout — see
/// <see cref="EmbeddingClientProfile"/>), not the sweep's <c>Batch</c> profile: a diagnostic
/// check should fail fast, not retry patiently, matching how interactive search degrades to
/// full-text rather than blocking on a backoff.
/// </remarks>
public sealed class RunEmbeddingLlmDiagnosticHandler(
    [FromKeyedServices(DefaultLlmChatClientKeys.SnippetEmbeddingsQuery)]
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    LlmSettings settings,
    IHostEnvironment environment,
    ILogger<RunEmbeddingLlmDiagnosticHandler> log
) : IEndpointHandler
{
    private const string DefaultText = "Zeeq snippet embedding diagnostic probe.";
    private const int MaxTextLength = 500;

    /// <summary>
    /// Runs a bounded local embedding-client smoke test.
    /// </summary>
    public async Task<
        Results<Ok<LlmEmbeddingDiagnosticResponse>, BadRequest<string>, NotFound>
    > HandleAsync(string? text, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        var status = SetupLlm.DescribeEmbeddingConfiguration(settings);

        if (status != LlmEmbeddingConfigurationStatus.Configured)
        {
            return TypedResults.Ok(
                new LlmEmbeddingDiagnosticResponse(
                    Success: false,
                    Model: settings.Embeddings.Model,
                    Dimensions: 0,
                    LatencyMs: 0,
                    ErrorCode: status switch
                    {
                        LlmEmbeddingConfigurationStatus.Disabled => "disabled",
                        LlmEmbeddingConfigurationStatus.MissingApiKey => "missing_api_key",
                        LlmEmbeddingConfigurationStatus.PlaceholderApiKey => "placeholder_api_key",
                        _ => "not_configured",
                    },
                    Message: DescribeStatus(status)
                )
            );
        }

        var boundedText = string.IsNullOrWhiteSpace(text) ? DefaultText : text.Trim();

        if (boundedText.Length > MaxTextLength)
        {
            return TypedResults.BadRequest($"Text must be {MaxTextLength} characters or fewer.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await embeddingGenerator.GenerateAsync(
                [boundedText],
                new EmbeddingGenerationOptions { Dimensions = settings.Embeddings.Dimensions },
                timeout.Token
            );

            stopwatch.Stop();

            var dimensions = result.Count > 0 ? result[0].Vector.Length : 0;

            log.LogInformation(
                "LLM embedding diagnostic succeeded. Model={Model}; Dimensions={Dimensions}; LatencyMs={LatencyMs}",
                settings.Embeddings.Model,
                dimensions,
                stopwatch.ElapsedMilliseconds
            );

            return TypedResults.Ok(
                new LlmEmbeddingDiagnosticResponse(
                    Success: true,
                    Model: settings.Embeddings.Model,
                    Dimensions: dimensions,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: null,
                    Message: "OK"
                )
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            return TypedResults.Ok(
                new LlmEmbeddingDiagnosticResponse(
                    Success: false,
                    Model: settings.Embeddings.Model,
                    Dimensions: 0,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: "timeout",
                    Message: "The embedding diagnostic timed out."
                )
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            log.LogWarning(
                ex,
                "LLM embedding diagnostic failed. Model={Model}; LatencyMs={LatencyMs}",
                settings.Embeddings.Model,
                stopwatch.ElapsedMilliseconds
            );

            return TypedResults.Ok(
                new LlmEmbeddingDiagnosticResponse(
                    Success: false,
                    Model: settings.Embeddings.Model,
                    Dimensions: 0,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: "provider_error",
                    Message: "The embedding diagnostic failed. Check zeeq-server logs for details."
                )
            );
        }
    }

    private static string DescribeStatus(LlmEmbeddingConfigurationStatus status) =>
        status switch
        {
            LlmEmbeddingConfigurationStatus.Disabled =>
                "Snippet embeddings are disabled (AppSettings:Llm:Embeddings:Enabled=false).",
            LlmEmbeddingConfigurationStatus.MissingApiKey =>
                "AppSettings:Llm:Embeddings:ApiKey is not set.",
            LlmEmbeddingConfigurationStatus.PlaceholderApiKey =>
                "AppSettings:Llm:Embeddings:ApiKey still holds its appsettings.json placeholder value.",
            _ => "Snippet embeddings are not configured.",
        };
}

/// <summary>
/// Sanitized response for the local embedding diagnostic endpoint.
/// </summary>
public sealed record LlmEmbeddingDiagnosticResponse(
    bool Success,
    string Model,
    int Dimensions,
    int LatencyMs,
    string? ErrorCode,
    string Message
);
