namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Indicates that a generated Pub/Sub topic or subscription id is invalid.
/// </summary>
public sealed class GcpPubSubResourceNameException(string resourceId, string rule)
    : InvalidOperationException($"Invalid Pub/Sub resource ID '{resourceId}': {rule}")
{
    /// <summary>
    /// Generated topic or subscription id that failed validation.
    /// </summary>
    public string ResourceId { get; } = resourceId;

    /// <summary>
    /// Human-readable Pub/Sub naming rule that was violated.
    /// </summary>
    public string Rule { get; } = rule;
}
