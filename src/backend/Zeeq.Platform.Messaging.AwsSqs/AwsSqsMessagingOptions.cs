using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>
/// Connection and queue settings for the standalone SQS adapter.
/// </summary>
public sealed record AwsSqsMessagingOptions
{
    /// <summary>Region used for endpoints and request signing.</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>Optional emulator endpoint; omitted for real AWS.</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>Deployment prefix, at most 16 ASCII letters, digits, hyphens or underscores.</summary>
    public required string QueuePrefix { get; init; }

    /// <summary>How Brighter handles missing queues. Validate does not provision them.</summary>
    public OnMissingChannel MissingChannelPolicy { get; init; } = OnMissingChannel.Create;

    /// <summary>SQS long-poll seconds, independent of Zeeq's empty-channel delay.</summary>
    public int LongPollSeconds { get; init; } = 20;
}
