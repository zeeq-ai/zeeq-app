using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.Postgres;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Creates Brighter Postgres connection configuration from Zeeq options.
/// </summary>
public sealed class PostgresMessagingConfiguration(
    string connectionString,
    PostgresMessagingOptions options
)
{
    /// <summary>
    /// Creates the Brighter relational database configuration used by producers and consumers.
    /// </summary>
    /// <returns>Brighter relational database configuration.</returns>
    public RelationalDatabaseConfiguration CreateRelationalConfiguration() =>
        new(
            connectionString: connectionString,
            queueStoreTable: options.QueueTables.Default,
            schemaName: options.SchemaName,
            binaryMessagePayload: options.BinaryMessagePayload
        );

    /// <summary>
    /// Creates the Brighter Postgres gateway connection wrapper.
    /// </summary>
    /// <returns>Postgres gateway connection.</returns>
    public PostgresMessagingGatewayConnection CreateGatewayConnection() =>
        new(CreateRelationalConfiguration());
}
