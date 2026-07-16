using Zeeq.Core.Common.AspNetCore.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Paramore.Brighter;

namespace Zeeq.Runtime.Server.Diagnostics;

/// <summary>
/// Development-only endpoints for manually smoke testing the message queue.
/// </summary>
/// <remarks>
/// These endpoints are intentionally anonymous so they can be exercised from
/// curl or Scalar while debugging a local Aspire stack. They are mapped only
/// when <c>IHostEnvironment.IsDevelopment()</c> is true, so production
/// hosts do not expose the route at all.
/// </remarks>
public sealed class MessageQueueDiagnosticEndpoints : IEndpoint
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
                "diagnostics/message-queue/smoke-test",
                static (
                    [FromQuery] string? note,
                    [FromServices] PublishMessageQueueDiagnosticHandler handler,
                    CancellationToken ct
                ) => handler.HandleAsync(note, ct)
            )
            .AllowAnonymous()
            .WithName("PublishMessageQueueDiagnostic")
            .WithTags("Diagnostics")
            .WithSummary("Smoke-test the message queue.")
            .WithDescription(
                """
                Publishes a small system message to exercise the queue end to end: publish,
                Postgres storage, Brighter dispatch, and handler execution. The response
                returns a `SmokeTestId` and confirms publishing only; the consumer logs a
                separate entry when it later runs, so check both the response and the
                `zeeq-server` logs. Pass an optional `note` to tag the payload.

                Development-only and anonymous so it can be exercised with `curl` or Scalar: the
                route is mapped solely when `IHostEnvironment.IsDevelopment()` is true and is
                absent on production hosts.
                """
            );
    }
}

/// <summary>
/// Publishes the diagnostic message used to verify queue startup and dispatch.
/// </summary>
/// <remarks>
/// The handler returns after the message is durably published to Brighter. The
/// consumer writes a separate log entry when the message is later consumed, so
/// manual testing should check both the HTTP response and the Aspire server logs.
/// </remarks>
public sealed class PublishMessageQueueDiagnosticHandler(
    IZeeqMessagePublisher publisher,
    IHostEnvironment environment
) : IEndpointHandler
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(
        typeof(PublishMessageQueueDiagnosticHandler)
    );

    /// <summary>
    /// Publishes the smoke-test message.
    /// </summary>
    /// <param name="note">Optional human-readable note included in the message payload.</param>
    /// <param name="cancellationToken">Cancellation token for the publish operation.</param>
    /// <returns>Accepted diagnostic message metadata, or 404 outside Development.</returns>
    public async Task<Results<Ok<MessageQueueDiagnosticResponse>, NotFound>> HandleAsync(
        string? note,
        CancellationToken cancellationToken
    )
    {
        if (!environment.IsDevelopment())
        {
            return TypedResults.NotFound();
        }

        var message = new MessageQueueDiagnosticMessage
        {
            SmokeTestId = $"mq_diag_{Guid.NewGuid():N}",
            Note = note,
            PublishedAtUtc = DateTimeOffset.UtcNow,
        };

        await publisher.PublishAsync(message, cancellationToken);

        var response = new MessageQueueDiagnosticResponse(
            message.SmokeTestId,
            message.PublishedAtUtc,
            "Published diagnostic system message. Check zeeq-server logs for the consumer entry."
        );

        Log.Here()
            .Information(
                "🛜  Message queue smoke test published. SmokeTestId: {SmokeTestId}; PublishedAtUtc: {PublishedAtUtc}",
                response.SmokeTestId,
                response.PublishedAtUtc
            );

        return TypedResults.Ok(response);
    }
}

/// <summary>
/// Response returned by the diagnostic publish endpoint.
/// </summary>
/// <remarks>
/// The response confirms that publishing completed. It does not prove the
/// consumer ran; use the returned <see cref="SmokeTestId"/> to find the matching
/// consumer log entry in Aspire.
/// </remarks>
public sealed record MessageQueueDiagnosticResponse(
    string SmokeTestId,
    DateTimeOffset PublishedAtUtc,
    string Message
);

/// <summary>
/// System message used by the local message queue smoke test.
/// </summary>
/// <remarks>
/// This message implements <see cref="ISystemMessage"/> so it routes through the
/// system queue table instead of tenant-tier buckets. It is intentionally small
/// and self-contained because its only purpose is validating local queue
/// publish, storage, dispatch, and handler execution.
/// </remarks>
[ConfigurePublisher(
    "diagnostics.message-queue.smoke-test",
    visibleTimeoutSeconds: 30,
    bufferSize: 1
)]
public sealed class MessageQueueDiagnosticMessage : Event, ISystemMessage
{
    /// <summary>
    /// Creates a diagnostic message with a Brighter identifier.
    /// </summary>
    public MessageQueueDiagnosticMessage()
        : base(Id.Random()) { }

    /// <summary>
    /// Human-readable identifier returned to the caller and logged by the consumer.
    /// </summary>
    public required string SmokeTestId { get; init; }

    /// <summary>
    /// Optional note supplied by the manual smoke-test caller.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// Timestamp captured before the message is published.
    /// </summary>
    public DateTimeOffset PublishedAtUtc { get; init; }
}

/// <summary>
/// Consumer for the local message queue smoke-test system message.
/// </summary>
/// <remarks>
/// Successful execution of this handler proves that the message was published,
/// stored in the Postgres system queue, picked up by Brighter, mapped back to
/// the typed payload, and dispatched through the Zeeq handler pipeline.
/// </remarks>
[ConfigureConsumer<MessageQueueDiagnosticMessage>(
    "diagnostics.message-queue.smoke-test.local",
    noOfPerformers: 1,
    bufferSize: 1,
    visibleTimeoutSeconds: 30,
    pollIntervalMilliseconds: 500
)]
public sealed class MessageQueueDiagnosticConsumer(IDeadLetterWriter deadLetterWriter)
    : ZeeqMessageHandler<MessageQueueDiagnosticMessage>(deadLetterWriter)
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext(
        typeof(MessageQueueDiagnosticConsumer)
    );

    /// <inheritdoc />
    protected override Task<MessageQueueDiagnosticMessage> HandleMessageAsync(
        MessageQueueDiagnosticMessage message,
        CancellationToken cancellationToken
    )
    {
        Log.Here()
            .Information(
                "✅  Message queue smoke test consumed. SmokeTestId: {SmokeTestId}",
                message.SmokeTestId
            );

        return Task.FromResult(message);
    }
}
