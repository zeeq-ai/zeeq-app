using Zeeq.Core.Common.AspNetCore.Contracts;
using Zeeq.Core.Llm;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Paramore.Brighter;

namespace Zeeq.Runtime.Server.Admin;

/// <summary>
/// Production system-admin diagnostics for validating platform dependencies.
/// </summary>
/// <remarks>
/// These endpoints are mapped under `/api/v1/admin` by the runtime endpoint
/// mapper. They inherit live system-admin authorization and hidden-route 404
/// behavior from the admin route group, and are intentionally separate from the
/// development-only anonymous diagnostics under `/api/v1/diagnostics`.
/// </remarks>
public sealed class AdminDiagnosticEndpoints : ISystemAdminEndpoint
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder app, IEndpointRouteBuilder rootApp)
    {
        var group = app.MapGroup("diagnostics");

        // POST /api/v1/admin/diagnostics/message-delivery
        group
            .MapPost(
                "message-delivery",
                static (
                    [FromServices] RunAdminMessageDeliveryDiagnosticHandler handler,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(cancellationToken)
            )
            .WithName("RunAdminMessageDeliveryDiagnostic")
            .WithTags("Admin")
            .WithSummary("Run the system message delivery diagnostic.")
            .WithDescription(
                """
                Publishes a system message, waits for the production message consumer to
                write a cache marker, and reports whether the marker appeared within the
                bounded diagnostic window.
                """
            );

        // POST /api/v1/admin/diagnostics/llm-default-key
        group
            .MapPost(
                "llm-default-key",
                static (
                    [FromServices] RunAdminLlmDefaultKeyDiagnosticHandler handler,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(cancellationToken)
            )
            .WithName("RunAdminLlmDefaultKeyDiagnostic")
            .WithTags("Admin")
            .WithSummary("Run the default Fast LLM key diagnostic.")
            .WithDescription(
                """
                Sends a short bounded prompt to the app-level default Fast LLM client and
                returns sanitized success or failure metadata. Successful responses include
                the returned joke text, which callers must treat as untrusted plain text.
                """
            );

        // POST /api/v1/admin/diagnostics/llm-embedding-key
        group
            .MapPost(
                "llm-embedding-key",
                static (
                    [FromServices] RunAdminLlmEmbeddingKeyDiagnosticHandler handler,
                    CancellationToken cancellationToken
                ) => handler.HandleAsync(cancellationToken)
            )
            .WithName("RunAdminLlmEmbeddingKeyDiagnostic")
            .WithTags("Admin")
            .WithSummary("Run the snippet embedding key diagnostic.")
            .WithDescription(
                """
                Sends a short bounded probe text to the Query-profile snippet embedding client
                and returns sanitized success or failure metadata. Reports a non-throwing
                "not configured" result (rather than calling the provider) when
                AppSettings:Llm:Embeddings:Enabled is false, the API key is unset, or the key
                still holds its appsettings.json placeholder value.
                """
            );
    }
}

/// <summary>
/// Runs the admin message-delivery diagnostic through the system queue path.
/// </summary>
public sealed partial class RunAdminMessageDeliveryDiagnosticHandler(
    IZeeqMessagePublisher publisher,
    HybridCache cache,
    ILogger<RunAdminMessageDeliveryDiagnosticHandler> logger
) : IEndpointHandler
{
    private const string CacheKeyPrefix = "admin:diagnostics:message-delivery:";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(500);

    private static readonly HybridCacheEntryOptions MarkerReadOptions = new()
    {
        Expiration = TimeSpan.FromMilliseconds(100),
        LocalCacheExpiration = TimeSpan.FromMilliseconds(1),
    };

    /// <summary>
    /// Publishes a diagnostic message and waits for the consumer cache marker.
    /// </summary>
    public async Task<Ok<AdminDiagnosticResponse>> HandleAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var runId = $"admin_mq_diag_{Guid.NewGuid():N}";
        var cacheKey = BuildCacheKey(runId);
        var message = new AdminMessageDeliveryDiagnosticMessage
        {
            RunId = runId,
            CacheKey = cacheKey,
            PublishedAtUtc = startedAtUtc,
        };

        await publisher.PublishAsync(message, cancellationToken);
        LogMessageDiagnosticPublished(logger, runId, startedAtUtc);

        while (DateTimeOffset.UtcNow - startedAtUtc < Timeout)
        {
            var marker = await ReadMarkerAsync(cache, cacheKey, cancellationToken);
            if (marker?.RunId == runId)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                LogMessageDiagnosticSucceeded(logger, runId, completedAtUtc);

                return TypedResults.Ok(
                    new AdminDiagnosticResponse(
                        Success: true,
                        Message: "Message delivery diagnostic succeeded.",
                        StartedAtUtc: startedAtUtc,
                        CompletedAtUtc: completedAtUtc,
                        Detail: "The system message consumer wrote the expected cache marker."
                    )
                );
            }

            await Task.Delay(PollDelay, cancellationToken);
        }

        var timedOutAtUtc = DateTimeOffset.UtcNow;
        LogMessageDiagnosticTimedOut(logger, runId, timedOutAtUtc);

        return TypedResults.Ok(
            new AdminDiagnosticResponse(
                Success: false,
                Message: "Message delivery diagnostic timed out.",
                StartedAtUtc: startedAtUtc,
                CompletedAtUtc: timedOutAtUtc,
                Detail: "The system message consumer did not write the expected marker within 30 seconds."
            )
        );
    }

    internal static string BuildCacheKey(string runId) => CacheKeyPrefix + runId;

    private static async ValueTask<AdminMessageDeliveryDiagnosticMarker?> ReadMarkerAsync(
        HybridCache cache,
        string cacheKey,
        CancellationToken cancellationToken
    ) =>
        await cache.GetOrCreateAsync<string, AdminMessageDeliveryDiagnosticMarker?>(
            key: cacheKey,
            state: cacheKey,
            factory: static (_, _) =>
                ValueTask.FromResult<AdminMessageDeliveryDiagnosticMarker?>(null),
            options: MarkerReadOptions,
            cancellationToken: cancellationToken
        );

    [LoggerMessage(
        EventId = 2800,
        Level = LogLevel.Information,
        Message = "Admin message-delivery diagnostic published. RunId={RunId}; StartedAtUtc={StartedAtUtc}"
    )]
    private static partial void LogMessageDiagnosticPublished(
        Microsoft.Extensions.Logging.ILogger logger,
        string runId,
        DateTimeOffset startedAtUtc
    );

    [LoggerMessage(
        EventId = 2801,
        Level = LogLevel.Information,
        Message = "Admin message-delivery diagnostic succeeded. RunId={RunId}; CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogMessageDiagnosticSucceeded(
        Microsoft.Extensions.Logging.ILogger logger,
        string runId,
        DateTimeOffset completedAtUtc
    );

    [LoggerMessage(
        EventId = 2802,
        Level = LogLevel.Warning,
        Message = "Admin message-delivery diagnostic timed out. RunId={RunId}; CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogMessageDiagnosticTimedOut(
        Microsoft.Extensions.Logging.ILogger logger,
        string runId,
        DateTimeOffset completedAtUtc
    );
}

/// <summary>
/// Runs the admin LLM key diagnostic against the default Fast client.
/// </summary>
public sealed partial class RunAdminLlmDefaultKeyDiagnosticHandler(
    DefaultLlmChatClients clients,
    ILogger<RunAdminLlmDefaultKeyDiagnosticHandler> logger
) : IEndpointHandler
{
    private const string Prompt = "Tell me a joke";

    /// <summary>
    /// Sends the diagnostic prompt with a bounded timeout.
    /// </summary>
    public async Task<Ok<AdminLlmDiagnosticResponse>> HandleAsync(
        CancellationToken cancellationToken
    )
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var response = await clients.Fast.GetResponseAsync(
                [new ChatMessage(ChatRole.User, Prompt)],
                new ChatOptions { MaxOutputTokens = 128, Temperature = 0.7f },
                timeout.Token
            );
            var completedAtUtc = DateTimeOffset.UtcNow;
            var joke = response.Text.Trim();

            LogLlmDiagnosticSucceeded(logger, completedAtUtc);

            return TypedResults.Ok(
                new AdminLlmDiagnosticResponse(
                    Success: true,
                    Message: "LLM default key diagnostic succeeded.",
                    StartedAtUtc: startedAtUtc,
                    CompletedAtUtc: completedAtUtc,
                    Detail: null,
                    Joke: joke
                )
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            LogLlmDiagnosticTimedOut(logger, completedAtUtc);

            return TypedResults.Ok(
                new AdminLlmDiagnosticResponse(
                    Success: false,
                    Message: "LLM default key diagnostic timed out.",
                    StartedAtUtc: startedAtUtc,
                    CompletedAtUtc: completedAtUtc,
                    Detail: "The Fast LLM client did not respond within 30 seconds.",
                    Joke: null
                )
            );
        }
        catch (Exception ex)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            LogLlmDiagnosticFailed(logger, ex, completedAtUtc);

            return TypedResults.Ok(
                new AdminLlmDiagnosticResponse(
                    Success: false,
                    Message: "LLM default key diagnostic failed.",
                    StartedAtUtc: startedAtUtc,
                    CompletedAtUtc: completedAtUtc,
                    Detail: "The provider call failed. Check zeeq-server logs for sanitized diagnostic details.",
                    Joke: null
                )
            );
        }
    }

    [LoggerMessage(
        EventId = 2810,
        Level = LogLevel.Information,
        Message = "Admin LLM default-key diagnostic succeeded. CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogLlmDiagnosticSucceeded(
        Microsoft.Extensions.Logging.ILogger logger,
        DateTimeOffset completedAtUtc
    );

    [LoggerMessage(
        EventId = 2811,
        Level = LogLevel.Warning,
        Message = "Admin LLM default-key diagnostic timed out. CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogLlmDiagnosticTimedOut(
        Microsoft.Extensions.Logging.ILogger logger,
        DateTimeOffset completedAtUtc
    );

    [LoggerMessage(
        EventId = 2812,
        Level = LogLevel.Warning,
        Message = "Admin LLM default-key diagnostic failed. CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogLlmDiagnosticFailed(
        Microsoft.Extensions.Logging.ILogger logger,
        Exception exception,
        DateTimeOffset completedAtUtc
    );
}

/// <summary>
/// Runs the admin snippet-embedding key diagnostic against the Query-profile embedding client.
/// </summary>
/// <remarks>
/// Uses the <c>Query</c> profile (near-zero SDK retries, short timeout), not the sweep's
/// <c>Batch</c> profile — a diagnostic check should fail fast, matching how interactive search
/// degrades to full-text rather than blocking on a retry backoff.
/// </remarks>
public sealed partial class RunAdminLlmEmbeddingKeyDiagnosticHandler(
    [FromKeyedServices(DefaultLlmChatClientKeys.SnippetEmbeddingsQuery)]
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    LlmSettings settings,
    ILogger<RunAdminLlmEmbeddingKeyDiagnosticHandler> logger
) : IEndpointHandler
{
    private const string ProbeText = "Zeeq snippet embedding key diagnostic probe.";

    /// <summary>
    /// Sends the diagnostic probe text with a bounded timeout.
    /// </summary>
    public async Task<Ok<AdminLlmEmbeddingDiagnosticResponse>> HandleAsync(
        CancellationToken cancellationToken
    )
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var status = SetupLlm.DescribeEmbeddingConfiguration(settings);

        if (status != LlmEmbeddingConfigurationStatus.Configured)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            LogLlmEmbeddingDiagnosticNotConfigured(logger, status, completedAtUtc);

            return TypedResults.Ok(
                new AdminLlmEmbeddingDiagnosticResponse(
                    Success: false,
                    Message: "Snippet embeddings are not configured.",
                    StartedAtUtc: startedAtUtc,
                    CompletedAtUtc: completedAtUtc,
                    Detail: DescribeStatus(status),
                    Dimensions: null
                )
            );
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var result = await embeddingGenerator.GenerateAsync(
                [ProbeText],
                new EmbeddingGenerationOptions { Dimensions = settings.Embeddings.Dimensions },
                timeout.Token
            );
            var completedAtUtc = DateTimeOffset.UtcNow;
            var dimensions = result.Count > 0 ? result[0].Vector.Length : 0;

            LogLlmEmbeddingDiagnosticSucceeded(logger, dimensions, completedAtUtc);

            return TypedResults.Ok(
                new AdminLlmEmbeddingDiagnosticResponse(
                    Success: true,
                    Message: "Snippet embedding key diagnostic succeeded.",
                    StartedAtUtc: startedAtUtc,
                    CompletedAtUtc: completedAtUtc,
                    Detail: null,
                    Dimensions: dimensions
                )
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            LogLlmEmbeddingDiagnosticTimedOut(logger, completedAtUtc);

            return TypedResults.Ok(
                new AdminLlmEmbeddingDiagnosticResponse(
                    Success: false,
                    Message: "Snippet embedding key diagnostic timed out.",
                    StartedAtUtc: startedAtUtc,
                    CompletedAtUtc: completedAtUtc,
                    Detail: "The embedding client did not respond within 10 seconds.",
                    Dimensions: null
                )
            );
        }
        catch (Exception ex)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            LogLlmEmbeddingDiagnosticFailed(logger, ex, completedAtUtc);

            return TypedResults.Ok(
                new AdminLlmEmbeddingDiagnosticResponse(
                    Success: false,
                    Message: "Snippet embedding key diagnostic failed.",
                    StartedAtUtc: startedAtUtc,
                    CompletedAtUtc: completedAtUtc,
                    Detail: "The provider call failed. Check zeeq-server logs for sanitized diagnostic details.",
                    Dimensions: null
                )
            );
        }
    }

    private static string DescribeStatus(LlmEmbeddingConfigurationStatus status) =>
        status switch
        {
            LlmEmbeddingConfigurationStatus.Disabled =>
                "AppSettings:Llm:Embeddings:Enabled is false.",
            LlmEmbeddingConfigurationStatus.MissingApiKey =>
                "AppSettings:Llm:Embeddings:ApiKey is not set.",
            LlmEmbeddingConfigurationStatus.PlaceholderApiKey =>
                "AppSettings:Llm:Embeddings:ApiKey still holds its appsettings.json placeholder value.",
            _ => "Snippet embeddings are not configured.",
        };

    [LoggerMessage(
        EventId = 2813,
        Level = LogLevel.Information,
        Message = "Admin LLM embedding-key diagnostic succeeded. Dimensions={Dimensions}; CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogLlmEmbeddingDiagnosticSucceeded(
        Microsoft.Extensions.Logging.ILogger logger,
        int dimensions,
        DateTimeOffset completedAtUtc
    );

    [LoggerMessage(
        EventId = 2814,
        Level = LogLevel.Warning,
        Message = "Admin LLM embedding-key diagnostic timed out. CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogLlmEmbeddingDiagnosticTimedOut(
        Microsoft.Extensions.Logging.ILogger logger,
        DateTimeOffset completedAtUtc
    );

    [LoggerMessage(
        EventId = 2815,
        Level = LogLevel.Warning,
        Message = "Admin LLM embedding-key diagnostic failed. CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogLlmEmbeddingDiagnosticFailed(
        Microsoft.Extensions.Logging.ILogger logger,
        Exception exception,
        DateTimeOffset completedAtUtc
    );

    [LoggerMessage(
        EventId = 2816,
        Level = LogLevel.Information,
        Message = "Admin LLM embedding-key diagnostic skipped: not configured. Status={Status}; CompletedAtUtc={CompletedAtUtc}"
    )]
    private static partial void LogLlmEmbeddingDiagnosticNotConfigured(
        Microsoft.Extensions.Logging.ILogger logger,
        LlmEmbeddingConfigurationStatus status,
        DateTimeOffset completedAtUtc
    );
}

/// <summary>
/// Shared response returned by admin diagnostics.
/// </summary>
public sealed record AdminDiagnosticResponse(
    bool Success,
    string Message,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? Detail = null
);

/// <summary>
/// Response returned by the admin snippet-embedding key diagnostic.
/// </summary>
public sealed record AdminLlmEmbeddingDiagnosticResponse(
    bool Success,
    string Message,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? Detail,
    int? Dimensions
);

/// <summary>
/// Response returned by the admin LLM diagnostic.
/// </summary>
public sealed record AdminLlmDiagnosticResponse(
    bool Success,
    string Message,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string? Detail,
    string? Joke
);

/// <summary>
/// Cache marker written by the message-delivery diagnostic consumer.
/// </summary>
public sealed record AdminMessageDeliveryDiagnosticMarker(
    string RunId,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset ConsumedAtUtc
);

/// <summary>
/// System message used by the production admin message-delivery diagnostic.
/// </summary>
[ConfigurePublisher("admin.diagnostics.message-delivery", visibleTimeoutSeconds: 30, bufferSize: 1)]
public sealed class AdminMessageDeliveryDiagnosticMessage : Event, ISystemMessage
{
    /// <summary>
    /// Creates the diagnostic message with a Brighter identifier.
    /// </summary>
    public AdminMessageDeliveryDiagnosticMessage()
        : base(Id.Random()) { }

    /// <summary>
    /// Unique run identifier returned in logs and cache marker.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Cache key where the consumer writes the completion marker.
    /// </summary>
    public required string CacheKey { get; init; }

    /// <summary>
    /// Timestamp captured before publish.
    /// </summary>
    public DateTimeOffset PublishedAtUtc { get; init; }
}

/// <summary>
/// Consumer that marks successful delivery of the admin diagnostic message.
/// </summary>
[ConfigureConsumer<AdminMessageDeliveryDiagnosticMessage>(
    "admin.diagnostics.message-delivery.local",
    noOfPerformers: 1,
    bufferSize: 1,
    visibleTimeoutSeconds: 30,
    pollIntervalMilliseconds: 500
)]
public sealed partial class AdminMessageDeliveryDiagnosticConsumer(
    HybridCache cache,
    IDeadLetterWriter deadLetterWriter,
    ILogger<AdminMessageDeliveryDiagnosticConsumer> logger
) : ZeeqMessageHandler<AdminMessageDeliveryDiagnosticMessage>(deadLetterWriter)
{
    private static readonly HybridCacheEntryOptions MarkerWriteOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMilliseconds(1),
    };

    /// <inheritdoc />
    protected override async Task<AdminMessageDeliveryDiagnosticMessage> HandleMessageAsync(
        AdminMessageDeliveryDiagnosticMessage message,
        CancellationToken cancellationToken
    )
    {
        var consumedAtUtc = DateTimeOffset.UtcNow;
        await cache.SetAsync(
            message.CacheKey,
            new AdminMessageDeliveryDiagnosticMarker(
                message.RunId,
                message.PublishedAtUtc,
                consumedAtUtc
            ),
            MarkerWriteOptions,
            cancellationToken: cancellationToken
        );

        LogMessageDiagnosticConsumed(logger, message.RunId, consumedAtUtc);

        return message;
    }

    [LoggerMessage(
        EventId = 2820,
        Level = LogLevel.Information,
        Message = "Admin message-delivery diagnostic consumed. RunId={RunId}; ConsumedAtUtc={ConsumedAtUtc}"
    )]
    private static partial void LogMessageDiagnosticConsumed(
        Microsoft.Extensions.Logging.ILogger logger,
        string runId,
        DateTimeOffset consumedAtUtc
    );
}
