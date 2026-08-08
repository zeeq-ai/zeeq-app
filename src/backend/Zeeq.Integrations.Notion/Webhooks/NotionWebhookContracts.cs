using System.Text.Json.Serialization;

namespace Zeeq.Integrations.Notion;

/// <summary>Notion's one-time, unsigned webhook verification challenge.</summary>
public sealed record NotionWebhookVerificationChallenge
{
    /// <summary>Secret that later signs webhook request bodies.</summary>
    [JsonPropertyName("verification_token")]
    public string? VerificationToken { get; init; }
}

/// <summary>Signed Notion webhook event envelope.</summary>
public sealed record NotionWebhookEvent
{
    /// <summary>Notion event identifier used for diagnostics.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Event type such as <c>page.content_updated</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Time Notion generated the event.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Webhook subscription that emitted the event.</summary>
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; init; }

    /// <summary>Notion workspace containing the event entity.</summary>
    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    /// <summary>Notion integration associated with the subscription.</summary>
    [JsonPropertyName("integration_id")]
    public string? IntegrationId { get; init; }

    /// <summary>Delivery attempt number.</summary>
    [JsonPropertyName("attempt_number")]
    public int AttemptNumber { get; init; }

    /// <summary>Entity changed by the event.</summary>
    [JsonPropertyName("entity")]
    public NotionWebhookEntity? Entity { get; init; }
}

/// <summary>Minimal identity of the Notion entity changed by an event.</summary>
public sealed record NotionWebhookEntity
{
    /// <summary>Notion entity identifier.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Entity type, such as <c>page</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
