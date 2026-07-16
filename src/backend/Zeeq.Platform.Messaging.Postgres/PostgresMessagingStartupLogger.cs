using System.Reflection;
using Zeeq.Core.Common;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Zeeq.Platform.Messaging.Postgres;

/// <summary>
/// Emits message queue startup diagnostics after the host has built the service provider.
/// </summary>
/// <remarks>
/// Brighter registration happens during service composition, before normal DI-backed
/// loggers are available. The setup extensions capture immutable snapshots of the
/// producer and consumer registrations, then this hosted service writes structured
/// Serilog telemetry during host startup so the entries show up in Aspire with
/// source-line context from <c>Log.Here()</c>.
///
/// This service is not a queue worker and does not keep running after startup.
/// The host calls <see cref="StartAsync(CancellationToken)"/> once, the service
/// emits the queue configuration telemetry, and then it returns immediately.
/// <see cref="StopAsync(CancellationToken)"/> is a no-op because this diagnostic
/// hook owns no background loop or external resource.
/// </remarks>
internal sealed class PostgresMessagingStartupLogger(
    IEnumerable<PostgresMessagingStartupSnapshot> snapshots
) : IHostedService
{
    private static readonly ILogger Log = Serilog.Log.ForContext(
        typeof(PostgresMessagingStartupLogger)
    );

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registrations = snapshots.ToArray();

        if (registrations.Length == 0)
        {
            return Task.CompletedTask;
        }

        var representative = registrations[0];

        var producerRegistration = registrations.Any(snapshot =>
            snapshot.Kind == PostgresMessagingRegistrationKind.Producer
        );

        var consumerRegistration = registrations.Any(snapshot =>
            snapshot.Kind == PostgresMessagingRegistrationKind.Consumer
        );

        var publicationCount = registrations.Sum(snapshot => snapshot.PublicationCount);
        var subscriptionCount = registrations.Sum(snapshot => snapshot.SubscriptionCount);

        var startupTelemetry = new PostgresMessagingStartupTelemetry(
            ProducerRegistration: producerRegistration,
            ConsumerRegistration: consumerRegistration,
            PublisherCount: representative.PublisherCount,
            HandlerCount: representative.ConsumerCount,
            PublicationCount: publicationCount,
            SubscriptionCount: subscriptionCount,
            SchemaName: representative.SchemaName,
            QueueTables: representative.QueueTables,
            BucketSummary: representative.BucketSummary,
            MissingChannelPolicy: representative.MissingChannelPolicy,
            BinaryMessagePayload: representative.BinaryMessagePayload,
            Assemblies: representative.Assemblies
        );

        Log.Here()
            .Information(
                "🛜  Postgres message queue configured. Producers: {ProducerRegistration}; Consumers: {ConsumerRegistration}; Publishers: {PublisherCount}; Handlers: {HandlerCount}; Publications: {PublicationCount}; Subscriptions: {SubscriptionCount}; Schema: {SchemaName}; MissingChannelPolicy: {MissingChannelPolicy}; BinaryPayload: {BinaryMessagePayload}",
                startupTelemetry.ProducerRegistration,
                startupTelemetry.ConsumerRegistration,
                startupTelemetry.PublisherCount,
                startupTelemetry.HandlerCount,
                startupTelemetry.PublicationCount,
                startupTelemetry.SubscriptionCount,
                startupTelemetry.SchemaName,
                startupTelemetry.MissingChannelPolicy,
                startupTelemetry.BinaryMessagePayload
            );

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Identifies which side of the Brighter transport registration produced a startup snapshot.
/// </summary>
/// <remarks>
/// Web mode can register both producers and inline consumers in the same process, while worker
/// mode registers consumers only. The startup logger uses this marker to collapse multiple
/// snapshots into one summary that states which registration paths are active.
/// </remarks>
internal enum PostgresMessagingRegistrationKind
{
    Producer,
    Consumer,
}

/// <summary>
/// Captures message queue registration details before the host starts.
/// </summary>
/// <remarks>
/// The setup extension creates one snapshot for each producer or consumer registration path
/// after the Brighter publications or subscriptions have been built. The snapshot materializes
/// formatted queue, bucket, assembly, publisher, and consumer details immediately so startup
/// logging reports the exact configuration that was registered.
/// </remarks>
internal readonly record struct PostgresMessagingStartupSnapshot
{
    public PostgresMessagingStartupSnapshot(
        PostgresMessagingRegistrationKind kind,
        IReadOnlyList<Assembly> assemblies,
        MessagingCatalog catalog,
        ZeeqMessagingOptions messagingOptions,
        PostgresMessagingOptions postgresOptions,
        int publicationCount,
        int subscriptionCount
    )
    {
        var routeBuilder = new PostgresMessagingRouteBuilder(
            messagingOptions.TenantBuckets,
            postgresOptions
        );

        Kind = kind;
        PublisherCount = catalog.Publishers.Count;
        ConsumerCount = catalog.Consumers.Count;
        PublicationCount = publicationCount;
        SubscriptionCount = subscriptionCount;
        SchemaName = postgresOptions.SchemaName;
        QueueTables = FormatQueueTables(postgresOptions.QueueTables);
        BucketSummary = FormatBuckets(messagingOptions.TenantBuckets);
        MissingChannelPolicy = postgresOptions.MissingChannelPolicy.ToString();
        BinaryMessagePayload = postgresOptions.BinaryMessagePayload;
        Assemblies = FormatAssemblies(assemblies);
        PublisherDetails = FormatPublishers(catalog, routeBuilder);
        ConsumerDetails = FormatConsumers(catalog, routeBuilder);
    }

    public PostgresMessagingRegistrationKind Kind { get; init; }

    public int PublisherCount { get; init; }

    public int ConsumerCount { get; init; }

    public int PublicationCount { get; init; }

    public int SubscriptionCount { get; init; }

    public string SchemaName { get; init; }

    public string QueueTables { get; init; }

    public string BucketSummary { get; init; }

    public string MissingChannelPolicy { get; init; }

    public bool BinaryMessagePayload { get; init; }

    public string Assemblies { get; init; }

    public IReadOnlyList<string> PublisherDetails { get; init; }

    public IReadOnlyList<string> ConsumerDetails { get; init; }

    private static string FormatQueueTables(PostgresQueueTableOptions tables) =>
        $"priority={tables.Priority}, default={tables.Default}, low={tables.Low}, system={tables.System}, immediate={tables.Immediate}";

    private static string FormatBuckets(TenantBucketRoutingOptions buckets) =>
        $"priority={buckets.PriorityBucketCount}, default={buckets.DefaultBucketCount}, low={buckets.LowBucketCount}";

    private static string FormatAssemblies(IReadOnlyList<Assembly> assemblies) =>
        string.Join(", ", assemblies.Select(assembly => assembly.GetName().Name).Order());

    private static string[] FormatPublishers(
        MessagingCatalog catalog,
        PostgresMessagingRouteBuilder routeBuilder
    ) =>
        catalog
            .Publishers.OrderBy(publisher => publisher.Topic)
            .ThenBy(publisher => publisher.MessageType.Name)
            .Select(publisher =>
                $"{publisher.MessageType.Name} -> {publisher.Topic}; routes={routeBuilder.BuildRoutes(publisher).Count}; priority={publisher.PriorityType.Name}; tenant={publisher.IsTenantMessage}; system={publisher.IsSystemMessage}; immediate={publisher.IsImmediateMessage}"
            )
            .ToArray();

    private static string[] FormatConsumers(
        MessagingCatalog catalog,
        PostgresMessagingRouteBuilder routeBuilder
    ) =>
        catalog
            .Consumers.OrderBy(consumer => consumer.ChannelName)
            .ThenBy(consumer => consumer.HandlerType.Name)
            .Select(consumer =>
                $"{consumer.HandlerType.Name} handles {consumer.MessageType.Name}; channel={consumer.ChannelName}; routes={GetConsumerRouteCount(catalog, routeBuilder, consumer)}; performers={consumer.NoOfPerformers}; buffer={consumer.BufferSize}; pollMs={consumer.PollIntervalMilliseconds}"
            )
            .ToArray();

    private static int GetConsumerRouteCount(
        MessagingCatalog catalog,
        PostgresMessagingRouteBuilder routeBuilder,
        MessagingConsumer consumer
    )
    {
        var publisher = catalog.FindPublisher(consumer.MessageType);

        return publisher is null ? 0 : routeBuilder.BuildRoutes(publisher).Count;
    }
}

/// <summary>
/// Aggregated startup telemetry emitted as the primary message queue log payload.
/// </summary>
/// <remarks>
/// <see cref="PostgresMessagingStartupLogger"/> combines the producer and consumer snapshots
/// into this compact value before destructuring it into Serilog. Publisher and consumer
/// detail lists are logged separately at debug level so the information is available locally
/// without making the summary line too large.
/// </remarks>
internal readonly record struct PostgresMessagingStartupTelemetry(
    bool ProducerRegistration,
    bool ConsumerRegistration,
    int PublisherCount,
    int HandlerCount,
    int PublicationCount,
    int SubscriptionCount,
    string SchemaName,
    string QueueTables,
    string BucketSummary,
    string MissingChannelPolicy,
    bool BinaryMessagePayload,
    string Assemblies
);
