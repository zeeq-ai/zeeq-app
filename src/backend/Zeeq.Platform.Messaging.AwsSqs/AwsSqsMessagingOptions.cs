using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>
/// Connection and queue settings for the SQS adapter.
/// </summary>
public sealed record AwsSqsMessagingOptions
{
    /// <summary>Runtime configuration section.</summary>
    public const string SectionName = "ZeeqMessaging:AwsSqs";

    /// <summary>Region used for endpoints and request signing.</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>Optional emulator endpoint; omitted for real AWS.</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>Deployment prefix, at most 16 ASCII letters, digits, hyphens or underscores.</summary>
    public required string QueuePrefix { get; init; }

    /// <summary>Missing-queue policy. Zeeq reconciles missing queues before Brighter validates them.</summary>
    public OnMissingChannel MissingChannelPolicy { get; init; } = OnMissingChannel.Validate;

    /// <summary>Allow runtime creation of missing queues. Disable when Pulumi owns the topology.</summary>
    public bool CreateMissingQueues { get; init; } = true;

    /// <summary>SQS long-poll seconds, independent of Zeeq's empty-channel delay.</summary>
    public int LongPollSeconds { get; init; } = 20;
}
