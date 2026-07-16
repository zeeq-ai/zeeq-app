using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Messaging.GcpPubSub;

/// <summary>
/// Emits Pub/Sub messaging startup diagnostics after the host is built.
/// </summary>
internal sealed class GcpPubSubMessagingStartupLogger(
    IEnumerable<GcpPubSubMessagingStartupSnapshot> snapshots,
    ILogger<GcpPubSubMessagingStartupLogger> logger
) : IHostedService
{
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
            snapshot.Kind == GcpPubSubMessagingRegistrationKind.Producer
        );
        var consumerRegistration = registrations.Any(snapshot =>
            snapshot.Kind == GcpPubSubMessagingRegistrationKind.Consumer
        );
        var publicationCount = registrations.Sum(snapshot => snapshot.PublicationCount);
        var subscriptionCount = registrations.Sum(snapshot => snapshot.SubscriptionCount);

        logger.LogInformation(
            "🛜 GCP Pub/Sub messaging configured. Producers: {ProducerRegistration}; Consumers: {ConsumerRegistration}; ProjectId: {ProjectId}; Emulator: {EmulatorStatus}; MissingChannelPolicy: {MissingChannelPolicy}; SubscriptionMode: {SubscriptionMode}; Publishers: {PublisherCount}; Handlers: {HandlerCount}; Publications: {PublicationCount}; Subscriptions: {SubscriptionCount}; Buckets: {BucketSummary}; Assemblies: {Assemblies}",
            producerRegistration,
            consumerRegistration,
            representative.ProjectId,
            representative.EmulatorStatus,
            representative.MissingChannelPolicy,
            representative.SubscriptionMode,
            representative.PublisherCount,
            representative.ConsumerCount,
            publicationCount,
            subscriptionCount,
            representative.BucketSummary,
            representative.Assemblies
        );

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Identifies which Pub/Sub registration path produced a startup snapshot.
/// </summary>
internal enum GcpPubSubMessagingRegistrationKind
{
    Producer,
    Consumer,
}

/// <summary>
/// Captures Pub/Sub registration details before the host starts.
/// </summary>
internal readonly record struct GcpPubSubMessagingStartupSnapshot
{
    public GcpPubSubMessagingStartupSnapshot(
        GcpPubSubMessagingRegistrationKind kind,
        IReadOnlyList<Assembly> assemblies,
        MessagingCatalog catalog,
        ZeeqMessagingOptions messagingOptions,
        GcpPubSubMessagingOptions pubSubOptions,
        int publicationCount,
        int subscriptionCount
    )
    {
        var expander = new MessagingRouteExpander(messagingOptions.TenantBuckets);

        Kind = kind;
        PublisherCount = catalog.Publishers.Count;
        ConsumerCount = catalog.Consumers.Count;
        PublicationCount = publicationCount;
        SubscriptionCount = subscriptionCount;
        ProjectId = pubSubOptions.ProjectId;
        EmulatorStatus = FormatEmulatorStatus(pubSubOptions);
        MissingChannelPolicy = pubSubOptions.MissingChannelPolicy.ToString();
        SubscriptionMode = pubSubOptions.SubscriptionMode.ToString();
        BucketSummary = FormatBuckets(messagingOptions.TenantBuckets);
        Assemblies = FormatAssemblies(assemblies);
        PublisherDetails = FormatPublishers(catalog, expander);
        ConsumerDetails = FormatConsumers(catalog, expander);
    }

    public GcpPubSubMessagingRegistrationKind Kind { get; init; }

    public int PublisherCount { get; init; }

    public int ConsumerCount { get; init; }

    public int PublicationCount { get; init; }

    public int SubscriptionCount { get; init; }

    public string ProjectId { get; init; }

    public string EmulatorStatus { get; init; }

    public string MissingChannelPolicy { get; init; }

    public string SubscriptionMode { get; init; }

    public string BucketSummary { get; init; }

    public string Assemblies { get; init; }

    public IReadOnlyList<string> PublisherDetails { get; init; }

    public IReadOnlyList<string> ConsumerDetails { get; init; }

    private static string FormatEmulatorStatus(GcpPubSubMessagingOptions options)
    {
        if (!options.UseEmulatorDetection)
        {
            return "disabled";
        }

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST"))
            ? "emulator-or-production"
            : "emulator";
    }

    private static string FormatBuckets(TenantBucketRoutingOptions buckets) =>
        $"priority={buckets.PriorityBucketCount}, default={buckets.DefaultBucketCount}, low={buckets.LowBucketCount}";

    private static string FormatAssemblies(IReadOnlyList<Assembly> assemblies) =>
        string.Join(", ", assemblies.Select(assembly => assembly.GetName().Name).Order());

    private static string[] FormatPublishers(
        MessagingCatalog catalog,
        MessagingRouteExpander expander
    ) =>
        catalog
            .Publishers.OrderBy(publisher => publisher.Topic)
            .ThenBy(publisher => publisher.MessageType.Name)
            .Select(publisher =>
                $"{publisher.MessageType.Name} -> {publisher.Topic}; routes={expander.BuildRoutes(publisher).Count}; priority={publisher.PriorityType.Name}; tenant={publisher.IsTenantMessage}; system={publisher.IsSystemMessage}; immediate={publisher.IsImmediateMessage}"
            )
            .ToArray();

    private static string[] FormatConsumers(
        MessagingCatalog catalog,
        MessagingRouteExpander expander
    ) =>
        catalog
            .Consumers.OrderBy(consumer => consumer.ChannelName)
            .ThenBy(consumer => consumer.HandlerType.Name)
            .Select(consumer =>
                $"{consumer.HandlerType.Name} handles {consumer.MessageType.Name}; channel={consumer.ChannelName}; routes={GetConsumerRouteCount(catalog, expander, consumer)}; performers={consumer.NoOfPerformers}; buffer={consumer.BufferSize}; pollMs={consumer.PollIntervalMilliseconds}"
            )
            .ToArray();

    private static int GetConsumerRouteCount(
        MessagingCatalog catalog,
        MessagingRouteExpander expander,
        MessagingConsumer consumer
    )
    {
        var publisher = catalog.FindPublisher(consumer.MessageType);

        return publisher is null ? 0 : expander.BuildRoutes(publisher).Count;
    }
}
