namespace Zeeq.Platform.Messaging;

/// <summary>
/// Fully resolved runtime settings for generated transport metadata.
/// </summary>
public sealed record ResolvedMessagingDefaults(
    int BufferSize,
    int NoOfPerformers,
    int VisibleTimeoutSeconds,
    int PollIntervalMilliseconds
);
