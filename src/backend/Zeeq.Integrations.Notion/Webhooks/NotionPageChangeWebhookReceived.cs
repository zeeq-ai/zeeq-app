using Paramore.Brighter;
using Zeeq.Core.Common;
using Zeeq.Platform.Messaging;

namespace Zeeq.Integrations.Notion;

/// <summary>Durable handoff for a validated Notion webhook event.</summary>
[ConfigurePublisher<PriorityMessage>("notion.webhook.page-change")]
public sealed class NotionPageChangeWebhookReceived : Event, ITenantMessage
{
    /// <summary>Creates the event with a generated message id.</summary>
    public NotionPageChangeWebhookReceived()
        : base(Id.Random()) { }

    /// <inheritdoc />
    public required string OrganizationId { get; init; }

    /// <inheritdoc />
    public string? TeamId { get; init; }

    /// <summary>Library receiving the page change.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Callback generation accepted at ingress.</summary>
    public int CallbackTokenSerial { get; init; }

    /// <summary>Notion webhook subscription id.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Notion workspace id.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Notion integration id, when supplied.</summary>
    public string? IntegrationId { get; init; }

    /// <summary>Notion event id used for diagnostics.</summary>
    public required string NotionEventId { get; init; }

    /// <summary>Notion event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Changed entity id.</summary>
    public required string EntityId { get; init; }

    /// <summary>Changed entity type.</summary>
    public required string EntityType { get; init; }

    /// <summary>Delivery attempt number.</summary>
    public int AttemptNumber { get; init; }

    /// <summary>Time Notion generated the event, when supplied.</summary>
    public DateTimeOffset? EventTimestamp { get; init; }

    /// <summary>Trace context captured at ingress.</summary>
    public required ZeeqTraceContext TraceContext { get; init; }
}
