namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Postgres transport options for Zeeq messaging.
/// </summary>
public sealed class PostgresMessagingOptions
{
    /// <summary>
    /// Configuration section name for binding.
    /// </summary>
    public const string SectionName = "ZeeqMessaging:Postgres";

    /// <summary>
    /// Schema containing active queue tables and dead-letter sink.
    /// </summary>
    public string SchemaName { get; init; } = "messaging";

    /// <summary>
    /// Stores queue payloads as JSONB when enabled.
    /// </summary>
    public bool BinaryMessagePayload { get; init; } = true;

    /// <summary>
    /// Missing channel policy for Brighter publications and subscriptions.
    /// </summary>
    public OnMissingChannelPolicy MissingChannelPolicy { get; init; } =
        OnMissingChannelPolicy.Validate;

    /// <summary>
    /// Active queue table mappings.
    /// </summary>
    public PostgresQueueTableOptions QueueTables { get; init; } = new();

    /// <summary>
    /// App-owned dead-letter sink table.
    /// </summary>
    public string DeadLetterTable { get; init; } = "brighter_messages_dead";
}
