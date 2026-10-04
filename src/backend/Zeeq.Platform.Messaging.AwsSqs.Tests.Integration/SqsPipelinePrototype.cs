using System.Collections.Concurrent;
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paramore.Brighter;
using Zeeq.Core.Models;
using IRequestContext = Paramore.Brighter.IRequestContext;

namespace Zeeq.Platform.Messaging.AwsSqs.Tests.Integration;

/// <summary>Runs a real Zeeq SQS messaging slice from either TUnit or standalone CSharpRepl.</summary>
public static class SqsPipelinePrototype
{
    /// <summary>Publishes system, immediate and tenant work, including an exhausted retry/fallback case.</summary>
    public static async Task<PrototypeResult> RunAsync(string endpoint)
    {
        if (!new Uri(endpoint).IsLoopback)
        {
            throw new ArgumentException(
                "The prototype requires a loopback emulator endpoint.",
                nameof(endpoint)
            );
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var prefix = $"zsq-{Guid.NewGuid():N}"[..14];
        var sqsOptions = new AwsSqsMessagingOptions
        {
            ServiceUrl = endpoint,
            QueuePrefix = prefix,
            LongPollSeconds = 1,
        };
        var messagingOptions = new ZeeqMessagingOptions
        {
            TenantBuckets = new TenantBucketRoutingOptions
            {
                PriorityBucketCount = 1,
                DefaultBucketCount = 2,
                LowBucketCount = 1,
            },
        };
        var credentials = new BasicAWSCredentials("test", "test");
        using var client = new AmazonSQSClient(
            credentials,
            new AmazonSQSConfig { ServiceURL = endpoint, AuthenticationRegion = sqsOptions.Region }
        );
        var topology = new AwsSqsMessagingTopology(
            new MessagingCatalogScanner().Scan(typeof(SqsPipelinePrototype).Assembly),
            messagingOptions,
            sqsOptions
        );
        var evidence = new PrototypeEvidence();
        IHost? host = null;
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.ConfigureContainer(
                new DefaultServiceProviderFactory(
                    new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
                )
            );
            builder.Services.AddSingleton(evidence);
            builder.Services.AddSingleton<IDeadLetterWriter>(evidence);
            builder.Services.AddSingleton<ITenantTierResolver, PrototypeTierResolver>();
            // Producer registration may create queues before host startup. The full validated
            // manifest is available above and every broker operation is inside this cleanup boundary.
            builder.Services.AddZeeqAwsSqsMessaging(
                messagingOptions,
                sqsOptions,
                credentials,
                registerConsumers: true,
                typeof(SqsPipelinePrototype).Assembly
            );
            host = builder.Build();
            await host.StartAsync(ct);
            using var scope = host.Services.CreateScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IZeeqMessagePublisher>();
            var resolver = scope.ServiceProvider.GetRequiredService<ZeeqMessageRouteResolver>();
            var router = new TenantBucketRouter();
            var organizations = Enumerable
                .Range(0, 100)
                .Select(index => $"org-prototype-{index}")
                .GroupBy(organization => router.ToBucket(organization, bucketCount: 2))
                .OrderBy(group => group.Key)
                .Select(group => group.First())
                .ToArray();
            if (organizations.Length != 2)
            {
                throw new InvalidOperationException(
                    "Prototype inputs must exercise both tenant buckets."
                );
            }

            var system = new PrototypeSystemMessage
            {
                Payload = "system-body",
                CorrelationId = Id.Random(),
            };
            var poison = new PrototypeSystemMessage
            {
                Payload = "poison-body",
                Fail = true,
                CorrelationId = Id.Random(),
            };
            var immediate = new PrototypeImmediateMessage
            {
                OrganizationId = organizations[0],
                Payload = "immediate-body",
                CorrelationId = Id.Random(),
            };
            var tenants = organizations
                .Select(organization => new PrototypeTenantMessage
                {
                    OrganizationId = organization,
                    Payload = organization,
                    CorrelationId = Id.Random(),
                })
                .ToArray();
            IReadOnlyList<IRequest> messages = [system, poison, immediate, .. tenants];
            var expectedRoutes = new Dictionary<string, string>
            {
                [system.Id.Value] = (await resolver.ResolveRouteAsync(system, ct)).RoutingKey,
                [poison.Id.Value] = (await resolver.ResolveRouteAsync(poison, ct)).RoutingKey,
                [immediate.Id.Value] = (await resolver.ResolveRouteAsync(immediate, ct)).RoutingKey,
            };
            foreach (var tenant in tenants)
            {
                expectedRoutes[tenant.Id.Value] = (
                    await resolver.ResolveRouteAsync(tenant, ct)
                ).RoutingKey;
            }

            await publisher.PublishAsync(system, ct);
            await publisher.PublishAsync(poison, ct);
            await publisher.PublishAsync(immediate, ct);
            foreach (var tenant in tenants)
            {
                await publisher.PublishAsync(tenant, ct);
            }

            while (messages.Any(message => !evidence.Outcomes.ContainsKey(message.Id.Value)))
            {
                await Task.Delay(millisecondsDelay: 50, ct);
            }
            foreach (var message in messages)
            {
                var outcome = evidence.Outcomes[message.Id.Value];
                var route = expectedRoutes[message.Id.Value];
                var physicalQueue = new AwsSqsResourceNameValidator().QueueName(prefix, route);
                if (
                    outcome.LogicalRoute != route
                    || outcome.ChannelName != physicalQueue
                    || outcome.HeaderTopic != physicalQueue
                    || outcome.MessageId != message.Id.Value
                    || outcome.CorrelationId != message.CorrelationId?.Value
                )
                {
                    throw new InvalidOperationException(
                        $"Message-pump metadata mismatch for '{route}': {outcome}"
                    );
                }
            }
            if (
                evidence.Outcomes[poison.Id.Value].Exception is not InvalidOperationException
                || evidence.Attempts[poison.Id.Value] != 4
                || evidence.Outcomes.Count != 5
                || evidence.Outcomes[system.Id.Value].Payload != system.Payload
                || evidence.Outcomes[poison.Id.Value].Payload != poison.Payload
                || evidence.Outcomes[immediate.Id.Value].Payload != immediate.Payload
                || tenants.Any(tenant =>
                    evidence.Outcomes[tenant.Id.Value].Payload != tenant.Payload
                )
            )
            {
                throw new InvalidOperationException(
                    "Handler body or retry/fallback evidence did not match."
                );
            }

            // Handler evidence is recorded before Brighter acknowledges; wait for broker state too.
            foreach (var publication in topology.Publications)
            {
                var url = (
                    await client.GetQueueUrlAsync(publication.ChannelName!.Value, ct)
                ).QueueUrl;
                while (true)
                {
                    var attributes = await client.GetQueueAttributesAsync(
                        new GetQueueAttributesRequest(
                            url,
                            ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"]
                        ),
                        ct
                    );
                    if (attributes.Attributes.Values.All(value => value == "0"))
                    {
                        break;
                    }
                    await Task.Delay(millisecondsDelay: 50, ct);
                }
            }

            var stopwatch = Stopwatch.StartNew();
            await host.StopAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new PrototypeResult(
                evidence.Outcomes.Count,
                evidence.Attempts[poison.Id.Value],
                topology.Publications.Count,
                expectedRoutes.Values.Distinct().Order().ToArray(),
                stopwatch.Elapsed.TotalSeconds
            );
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if (host is not null)
                {
                    await host.StopAsync(cleanup.Token).WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                try
                {
                    // All potential queues are known before provisioning, including partial startup.
                    foreach (var publication in topology.Publications)
                    {
                        try
                        {
                            var url = (
                                await client.GetQueueUrlAsync(
                                    publication.ChannelName!.Value,
                                    cleanup.Token
                                )
                            ).QueueUrl;
                            await client.DeleteQueueAsync(url, cleanup.Token);
                        }
                        catch (QueueDoesNotExistException)
                        {
                            // A failed startup may have provisioned only part of the test topology.
                        }
                    }
                }
                finally
                {
                    host?.Dispose();
                }
            }
        }
    }
}

public sealed record PrototypeResult(
    int MessagesHandled,
    int PoisonAttempts,
    int QueuesCreated,
    IReadOnlyList<string> Routes,
    double ShutdownSeconds
);

public sealed record PrototypeOutcome(
    string MessageId,
    string? CorrelationId,
    string LogicalRoute,
    string ChannelName,
    string HeaderTopic,
    string Payload,
    Exception? Exception
);

/// <summary>Records handler and fallback evidence; this sink is confined to the prototype assembly.</summary>
public sealed class PrototypeEvidence : IDeadLetterWriter
{
    public ConcurrentDictionary<string, int> Attempts { get; } = new();
    public ConcurrentDictionary<string, PrototypeOutcome> Outcomes { get; } = new();

    public void Attempt(IRequest message) =>
        Attempts.AddOrUpdate(message.Id.Value, addValue: 1, (_, count) => count + 1);

    public void Record(
        IRequest message,
        IRequestContext? context,
        string payload,
        Exception? exception = null
    )
    {
        var originating =
            context?.OriginatingMessage
            ?? throw new InvalidOperationException("Missing originating message.");
        Outcomes[message.Id.Value] = new PrototypeOutcome(
            originating.Header.MessageId.Value,
            originating.Header.CorrelationId.Value,
            originating.Header.Bag[AwsSqsMessagingTopology.LogicalRouteHeader].ToString()!,
            context!.Bag["ChannelName"].ToString()!,
            originating.Header.Topic.Value,
            payload,
            exception
        );
    }

    public Task WriteAsync<TMessage>(
        TMessage message,
        IRequestContext? context,
        Exception? exception,
        CancellationToken cancellationToken = default
    )
        where TMessage : class, IRequest
    {
        Record(message, context, ((PrototypeSystemMessage)(IRequest)message).Payload, exception);
        return Task.CompletedTask;
    }
}

public sealed class PrototypeTierResolver : ITenantTierResolver
{
    public ValueTask<OrganizationTier> ResolveTierAsync(
        string organizationId,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(OrganizationTier.Default);
}

[ConfigurePublisher("prototype.system")]
public sealed class PrototypeSystemMessage() : Event(Id.Random()), ISystemMessage
{
    public string Payload { get; set; } = "";
    public bool Fail { get; set; }
}

[ConfigurePublisher("prototype.tenant")]
public sealed class PrototypeTenantMessage() : Event(Id.Random()), ITenantMessage
{
    public string OrganizationId { get; set; } = "";
    public string? TeamId { get; set; }
    public string Payload { get; set; } = "";
}

[ConfigurePublisher<ImmediateMessage>("prototype.immediate")]
public sealed class PrototypeImmediateMessage() : Event(Id.Random()), ITenantMessage
{
    public string OrganizationId { get; set; } = "";
    public string? TeamId { get; set; }
    public string Payload { get; set; } = "";
}

[ConfigureConsumer<PrototypeSystemMessage>(
    "prototype.system.handler",
    noOfPerformers: 1,
    bufferSize: 1,
    pollIntervalMilliseconds: 100
)]
public sealed class PrototypeSystemHandler(PrototypeEvidence evidence)
    : ZeeqMessageHandler<PrototypeSystemMessage>(evidence)
{
    protected override Task<PrototypeSystemMessage> HandleMessageAsync(
        PrototypeSystemMessage message,
        CancellationToken cancellationToken
    )
    {
        evidence.Attempt(message);
        if (message.Fail)
        {
            throw new InvalidOperationException("prototype-handler-failure");
        }
        evidence.Record(message, Context, message.Payload);
        return Task.FromResult(message);
    }
}

[ConfigureConsumer<PrototypeTenantMessage>(
    "prototype.tenant.handler",
    noOfPerformers: 1,
    bufferSize: 1,
    pollIntervalMilliseconds: 100
)]
public sealed class PrototypeTenantHandler(PrototypeEvidence evidence)
    : ZeeqMessageHandler<PrototypeTenantMessage>(evidence)
{
    protected override Task<PrototypeTenantMessage> HandleMessageAsync(
        PrototypeTenantMessage message,
        CancellationToken cancellationToken
    )
    {
        evidence.Attempt(message);
        evidence.Record(message, Context, message.Payload);
        return Task.FromResult(message);
    }
}

[ConfigureConsumer<PrototypeImmediateMessage>(
    "prototype.immediate.handler",
    noOfPerformers: 1,
    bufferSize: 1,
    pollIntervalMilliseconds: 100
)]
public sealed class PrototypeImmediateHandler(PrototypeEvidence evidence)
    : ZeeqMessageHandler<PrototypeImmediateMessage>(evidence)
{
    protected override Task<PrototypeImmediateMessage> HandleMessageAsync(
        PrototypeImmediateMessage message,
        CancellationToken cancellationToken
    )
    {
        evidence.Attempt(message);
        evidence.Record(message, Context, message.Payload);
        return Task.FromResult(message);
    }
}
