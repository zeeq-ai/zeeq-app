using Google.Protobuf.Collections;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Shared Pub/Sub labels applied to Zeeq-managed messaging resources.
/// </summary>
public static class GcpPubSubTopologyLabels
{
    /// <summary>
    /// Label key used to identify resources managed by Zeeq.
    /// </summary>
    public const string ManagedByKey = "managed_by";

    /// <summary>
    /// Label value used to identify resources managed by Zeeq.
    /// </summary>
    public const string ManagedByValue = "zeeq";

    /// <summary>
    /// Adds the Zeeq managed-resource label to a dictionary copy.
    /// </summary>
    /// <param name="labels">Optional source labels.</param>
    /// <returns>A new label dictionary with the Zeeq managed-resource label.</returns>
    public static Dictionary<string, string> ApplyTo(
        IReadOnlyDictionary<string, string>? labels = null
    )
    {
        var result = labels is null ? [] : new Dictionary<string, string>(labels);
        result[ManagedByKey] = ManagedByValue;

        return result;
    }

    /// <summary>
    /// Adds the Zeeq managed-resource label to a Pub/Sub map field copy.
    /// </summary>
    /// <param name="labels">Optional source labels.</param>
    /// <returns>A new label map with the Zeeq managed-resource label.</returns>
    public static MapField<string, string> ToMapField(
        IReadOnlyDictionary<string, string>? labels = null
    )
    {
        var result = new MapField<string, string>();

        foreach (var (key, value) in ApplyTo(labels))
        {
            result[key] = value;
        }

        return result;
    }
}
