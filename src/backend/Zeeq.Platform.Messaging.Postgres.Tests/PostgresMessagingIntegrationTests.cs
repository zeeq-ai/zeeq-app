using System.Collections.Concurrent;
using System.Diagnostics;
using Zeeq.Core.Models;
using Zeeq.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using Paramore.Brighter;

namespace Zeeq.Platform.Messaging.Postgres.Tests;

/// <summary>
/// Integration tests for the Brighter PostgreSQL message transport.
///
/// Run:
/// dotnet run --project src/backend/Zeeq.Platform.Messaging.Postgres.Tests --output detailed --disable-logo --treenode-filter "/*/*/PostgresMessagingIntegrationTests/*"
/// </summary>
[Property("integration", "true")]
[Property("testcontainer", "true")]
[ClassDataSource<PgDatabaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class PostgresMessagingIntegrationTests(PgDatabaseFixture postgres)
{
    private const string Topic = "diagnostics.integration.smoke";
    private const string Queue = $"{Topic}.system";

    /// <summary>
    /// Proves that the Postgres transport persists trace headers and dispatches to a real consumer.
    /// </summary>
    /// <remarks>
    /// The test deliberately inspects the queue row before starting the hosted
    /// consumer. That verifies Brighter's serialized message contains the W3C
    /// trace context, then the consumer start proves the same row can be read,
    /// mapped back to the typed message, handled, and acknowledged.
    /// </remarks>
    [Test]
    public async Task PublishAsync_WithSystemMessage_PersistsTraceHeaderAndConsumesMessage()
    {
        await ClearQueueAsync();

        var smokeTestId = $"test_{Guid.NewGuid():N}";
        var handled = IntegrationMessageObserver.Watch(smokeTestId);
        using var listener = new BrighterActivityListener();
        await using var provider = BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IZeeqMessagePublisher>();

        await publisher.PublishAsync(
            new IntegrationSystemMessage { SmokeTestId = smokeTestId },
            CancellationToken.None
        );

        var queuedMessage = await ReadQueuedMessageAsync(smokeTestId);
        await Assert.That(queuedMessage.Content).IsNotNull();
        await Assert.That(queuedMessage.TraceParent).IsNotNull();
        await Assert.That(queuedMessage.TraceParent).IsNotEmpty();

        var hostedServices = await StartHostedServicesAsync(provider);

        try
        {
            await WaitForHandledAsync(handled);

            await WaitForQueueDrainAsync(smokeTestId);
            await Assert.That(listener.StoppedActivities.Count).IsGreaterThan(0);
        }
        finally
        {
            await StopHostedServicesAsync(hostedServices);
            IntegrationMessageObserver.Forget(smokeTestId);
            await ClearQueueAsync();
        }
    }

    /// <summary>
    /// Proves that immediate-priority tenant messages use the shared immediate table.
    /// </summary>
    [Test]
    public async Task PublishAsync_WithImmediateMessage_PersistsToImmediateQueue()
    {
        var smokeTestId = $"test_{Guid.NewGuid():N}";
        await ClearImmediateQueueAsync();
        await using var provider = BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var publisher = scope.ServiceProvider.GetRequiredService<IZeeqMessagePublisher>();

        await publisher.PublishAsync(
            new IntegrationImmediateMessage
            {
                OrganizationId = "org_immediate",
                TeamId = "team_immediate",
                SmokeTestId = smokeTestId,
            },
            CancellationToken.None
        );

        var queuedRows = await CountImmediateQueueRowsAsync(smokeTestId);

        await Assert.That(queuedRows).IsEqualTo(1);
    }

    private ServiceProvider BuildServiceProvider()
    {
        ConfigureTracePropagation();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton<ITenantTierResolver, DefaultTierResolver>();
        services.AddZeeqPostgresMessageProducers(
            postgres.ConnectionString,
            MessagingOptions(),
            PostgresOptions(),
            typeof(PostgresMessagingIntegrationTests).Assembly
        );
        services.AddZeeqPostgresMessageConsumers(
            postgres.ConnectionString,
            MessagingOptions(),
            PostgresOptions(),
            typeof(PostgresMessagingIntegrationTests).Assembly
        );

        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task ClearQueueAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM messaging.brighter_messages_system
            WHERE queue = @queue
            """;
        command.Parameters.AddWithValue("queue", Queue);

        await command.ExecuteNonQueryAsync();
    }

    private async Task ClearImmediateQueueAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM messaging.brighter_messages_immediate
            WHERE queue = @queue
            """;
        command.Parameters.AddWithValue("queue", "diagnostics.integration.immediate.immediate");

        await command.ExecuteNonQueryAsync();
    }

    private async Task<QueuedMessage> ReadQueuedMessageAsync(string smokeTestId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                content -> 'header' ->> 'traceParent',
                content::text
            FROM messaging.brighter_messages_system
            WHERE queue = @queue
              AND content::text LIKE @smoke_test_id
            ORDER BY id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("queue", Queue);
        command.Parameters.AddWithValue("smoke_test_id", $"%{smokeTestId}%");

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new(null, null);
        }

        var traceParent = reader.IsDBNull(0) ? null : reader.GetString(0);
        var content = reader.IsDBNull(1) ? null : reader.GetString(1);

        return new(traceParent, content);
    }

    private async Task<int> CountQueueRowsAsync(string smokeTestId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM messaging.brighter_messages_system
            WHERE queue = @queue
              AND content::text LIKE @smoke_test_id
            """;
        command.Parameters.AddWithValue("queue", Queue);
        command.Parameters.AddWithValue("smoke_test_id", $"%{smokeTestId}%");

        var count = await command.ExecuteScalarAsync();

        return Convert.ToInt32(count);
    }

    private async Task<int> CountImmediateQueueRowsAsync(string smokeTestId)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM messaging.brighter_messages_immediate
            WHERE queue = @queue
              AND content::text LIKE @smoke_test_id
            """;
        command.Parameters.AddWithValue("queue", "diagnostics.integration.immediate.immediate");
        command.Parameters.AddWithValue("smoke_test_id", $"%{smokeTestId}%");

        var count = await command.ExecuteScalarAsync();

        return Convert.ToInt32(count);
    }

    private static async Task<IReadOnlyList<IHostedService>> StartHostedServicesAsync(
        IServiceProvider provider
    )
    {
        var services = provider.GetServices<IHostedService>().ToArray();

        foreach (var service in services)
        {
            await service.StartAsync(CancellationToken.None);
        }

        return services;
    }

    private static async Task StopHostedServicesAsync(IReadOnlyList<IHostedService> services)
    {
        foreach (var service in services.Reverse())
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForHandledAsync(Task task)
    {
        var timeout = Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);

        var completed = await Task.WhenAny(task, timeout);
        await Assert.That(completed).IsEqualTo(task);
        await task;
    }

    private async Task WaitForQueueDrainAsync(string smokeTestId)
    {
        var deadline = TimeProvider.System.GetUtcNow().AddSeconds(10);

        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            var remainingRows = await CountQueueRowsAsync(smokeTestId);
            if (remainingRows == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        await Assert.That(await CountQueueRowsAsync(smokeTestId)).IsEqualTo(0);
    }

    private static ZeeqMessagingOptions MessagingOptions() =>
        new()
        {
            TenantBuckets = new TenantBucketRoutingOptions
            {
                PriorityBucketCount = 1,
                DefaultBucketCount = 1,
                LowBucketCount = 1,
            },
        };

    private static PostgresMessagingOptions PostgresOptions() => new();

    private static void ConfigureTracePropagation()
    {
        Sdk.SetDefaultTextMapPropagator(
            new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()])
        );
    }

    private sealed record QueuedMessage(string? TraceParent, string? Content);

    private sealed class DefaultTierResolver : ITenantTierResolver
    {
        public ValueTask<OrganizationTier> ResolveTierAsync(
            string organizationId,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(OrganizationTier.Default);
    }

    [ConfigurePublisher(Topic, visibleTimeoutSeconds: 10, bufferSize: 1)]
    public sealed class IntegrationSystemMessage : Event, ISystemMessage
    {
        public IntegrationSystemMessage()
            : base(Id.Random()) { }

        public required string SmokeTestId { get; init; }
    }

    [ConfigurePublisher<ImmediateMessage>("diagnostics.integration.immediate")]
    public sealed class IntegrationImmediateMessage : Event, ITenantMessage
    {
        public IntegrationImmediateMessage()
            : base(Id.Random()) { }

        public required string OrganizationId { get; init; }

        public string? TeamId { get; init; }

        public required string SmokeTestId { get; init; }
    }

    [ConfigureConsumer<IntegrationSystemMessage>(
        "diagnostics.integration.smoke.consumer",
        noOfPerformers: 1,
        bufferSize: 1,
        visibleTimeoutSeconds: 10,
        pollIntervalMilliseconds: 100
    )]
    public sealed class IntegrationSystemMessageConsumer(IDeadLetterWriter deadLetterWriter)
        : ZeeqMessageHandler<IntegrationSystemMessage>(deadLetterWriter)
    {
        protected override Task<IntegrationSystemMessage> HandleMessageAsync(
            IntegrationSystemMessage message,
            CancellationToken cancellationToken
        )
        {
            IntegrationMessageObserver.MarkHandled(message.SmokeTestId);

            return Task.FromResult(message);
        }
    }

    private sealed class BrighterActivityListener : IDisposable
    {
        private readonly ActivityListener _listener;

        public BrighterActivityListener()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Paramore.Brighter",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => StoppedActivities.Enqueue(activity.DisplayName),
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<string> StoppedActivities { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    private static class IntegrationMessageObserver
    {
        private static readonly ConcurrentDictionary<string, TaskCompletionSource> Handled = new();

        public static Task Watch(string smokeTestId)
        {
            var source = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            if (!Handled.TryAdd(smokeTestId, source))
            {
                throw new InvalidOperationException(
                    $"Duplicate smoke test id registered: {smokeTestId}"
                );
            }

            return source.Task;
        }

        public static void MarkHandled(string smokeTestId)
        {
            if (Handled.TryGetValue(smokeTestId, out var source))
            {
                source.TrySetResult();
            }
        }

        public static void Forget(string smokeTestId) => Handled.TryRemove(smokeTestId, out _);
    }
}
