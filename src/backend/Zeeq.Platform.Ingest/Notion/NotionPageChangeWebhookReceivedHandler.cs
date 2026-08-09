using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;
using Zeeq.Integrations.Notion;
using Zeeq.Platform.Messaging;

namespace Zeeq.Platform.Ingest;

/// <summary>Routes validated Notion page events into idempotent document mutations.</summary>
[ConfigureConsumer<NotionPageChangeWebhookReceived>(
    "notion.webhook.page-change.ingest",
    noOfPerformers: 1,
    bufferSize: 10,
    visibleTimeoutSeconds: 60,
    pollIntervalMilliseconds: 50
)]
public sealed partial class NotionPageChangeWebhookReceivedHandler(
    IDeadLetterWriter deadLetterWriter,
    INotionWebhookStore webhookState,
    ILibraryDocumentStore libraries,
    IExternalPendingContentSyncStore pendingContent,
    ILogger<NotionPageChangeWebhookReceivedHandler> logger
) : ZeeqMessageHandler<NotionPageChangeWebhookReceived>(deadLetterWriter)
{
    /// <inheritdoc />
    protected override async Task<NotionPageChangeWebhookReceived> HandleMessageAsync(
        NotionPageChangeWebhookReceived message,
        CancellationToken cancellationToken
    )
    {
        using var activity = StartActivity(message);
        var observation = await webhookState.ObserveSignedEventAsync(
            message.OrganizationId,
            message.LibraryId,
            message.CallbackTokenSerial,
            message.WorkspaceId,
            message.SubscriptionId,
            DateTimeOffset.UtcNow,
            cancellationToken
        );

        if (
            observation
            is not (
                NotionWebhookEventObservationResult.Activated
                or NotionWebhookEventObservationResult.Current
            )
        )
        {
            WebhooksNoOp.Add(
                1,
                new KeyValuePair<string, object?>("reason", observation.ToString())
            );
            LogWebhookIgnored(
                logger,
                message.NotionEventId,
                message.LibraryId,
                observation.ToString()
            );
            return message;
        }

        if (!string.Equals(message.EntityType, "page", StringComparison.Ordinal))
        {
            WebhooksNoOp.Add(1, new KeyValuePair<string, object?>("reason", "non_page"));
            return message;
        }

        var action = GetPageEventAction(message.EventType);
        if (action is NotionPageEventAction.Delete)
        {
            await libraries.DeleteDocumentByExternalIdAsync(
                message.OrganizationId,
                message.LibraryId,
                message.EntityId,
                cancellationToken
            );
            WebhooksConsumed.Add(1, new KeyValuePair<string, object?>("action", "delete"));
            return message;
        }

        if (action is NotionPageEventAction.Dirty)
        {
            await pendingContent.UpsertAsync(
                message.OrganizationId,
                message.LibraryId,
                message.EntityId,
                message.EventType,
                message.EventTimestamp ?? DateTimeOffset.UtcNow,
                cancellationToken
            );
            WebhooksConsumed.Add(1, new KeyValuePair<string, object?>("action", "dirty"));
        }
        else
        {
            WebhooksNoOp.Add(1, new KeyValuePair<string, object?>("reason", "event_ignored"));
        }

        return message;
    }

    private static NotionPageEventAction GetPageEventAction(string eventType) =>
        eventType switch
        {
            "page.created" => NotionPageEventAction.Dirty,
            "page.undeleted" => NotionPageEventAction.Dirty,
            "page.content_updated" => NotionPageEventAction.Dirty,
            "page.properties_updated" => NotionPageEventAction.Dirty,
            "page.moved" => NotionPageEventAction.Dirty,
            "page.deleted" => NotionPageEventAction.Delete,
            _ => NotionPageEventAction.Ignored,
        };

    private enum NotionPageEventAction
    {
        Ignored = 0,
        Dirty = 1,
        Delete = 2,
    }

    private static readonly Counter<long> WebhooksConsumed =
        ZeeqTelemetry.Metrics.CreateCounter<long>("zeeq.notion.webhook.consumed");

    private static readonly Counter<long> WebhooksNoOp = ZeeqTelemetry.Metrics.CreateCounter<long>(
        "zeeq.notion.webhook.no_op"
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ignoring stale or mismatched Notion webhook event {NotionEventId} for library {LibraryId}: {Observation}."
    )]
    private static partial void LogWebhookIgnored(
        ILogger logger,
        string notionEventId,
        string libraryId,
        string observation
    );

    private static Activity? StartActivity(NotionPageChangeWebhookReceived message)
    {
        ZeeqTelemetry.TryParseTraceContext(message.TraceContext, out var parentContext);

        return ZeeqTelemetry.Tracer.StartActivity(
            $"notion.webhook.{message.EventType}.consume",
            ActivityKind.Consumer,
            parentContext,
            tags:
            [
                new("notion.event.id", message.NotionEventId),
                new("notion.event.type", message.EventType),
                new("notion.entity.id", message.EntityId),
                new("notion.attempt_number", message.AttemptNumber),
                new("library.id", message.LibraryId),
                new("organization.id", message.OrganizationId),
            ]
        );
    }
}
