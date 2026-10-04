using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Paramore.Brighter;
using Paramore.Brighter.ServiceActivator;
using Zeeq.Platform.Messaging.GcpPubSub;

namespace Zeeq.Platform.Messaging.AwsSqs.Tests.Integration;

/// <summary>Exercises role separation and GCP-compatible startup reconciliation against Floci.</summary>
[ClassDataSource<FlociFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel("SqsPipeline")]
public sealed class SqsRuntimeSetupTests(FlociFixture fixture)
{
    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Registration_InstallsOnlySelectedProcessRoles(bool producers, bool consumers)
    {
        await using var queues = new SqsTestQueue(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = fixture.Options(queues.Prefix);
        var messaging = MessagingOptions();
        var topology = new AwsSqsMessagingTopology(
            new MessagingCatalogScanner().Scan(typeof(SqsPipelinePrototype).Assembly),
            messaging,
            options
        );
        foreach (var publication in topology.Publications)
        {
            queues.Track(publication.ChannelName!.Value);
        }
        var builder = Host.CreateApplicationBuilder();
        builder.ConfigureContainer(
            new DefaultServiceProviderFactory(
                new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
            )
        );
        var evidence = new PrototypeEvidence();
        builder.Services.AddSingleton(evidence);
        builder.Services.AddSingleton<IDeadLetterWriter>(evidence);
        builder.Services.AddScoped<ITenantTierResolver, PrototypeTierResolver>();
        if (!producers)
        {
            builder.Services.AddZeeqAwsSqsMessageConsumers(
                messaging,
                options,
                fixture.Credentials,
                deadline.Token,
                typeof(SqsPipelinePrototype).Assembly
            );
        }
        else if (!consumers)
        {
            builder.Services.AddZeeqAwsSqsMessageProducers(
                messaging,
                options,
                fixture.Credentials,
                deadline.Token,
                typeof(SqsPipelinePrototype).Assembly
            );
        }
        else
        {
            builder.Services.AddZeeqAwsSqsMessaging(
                messaging,
                options,
                fixture.Credentials,
                registerConsumers: true,
                cancellationToken: deadline.Token,
                typeof(SqsPipelinePrototype).Assembly
            );
        }
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        await Assert
            .That(scope.ServiceProvider.GetService<IZeeqMessagePublisher>() is not null)
            .IsEqualTo(producers);
        await Assert.That(host.Services.GetService<IDispatcher>() is not null).IsEqualTo(consumers);
        await Assert
            .That(
                host.Services.GetServices<IHostedService>()
                    .Any(x => x is BrighterMessagingConsumerHostedService)
            )
            .IsEqualTo(consumers);
        if (producers)
        {
            var message = new PrototypeSystemMessage { Payload = "producer-role-body" };
            await scope
                .ServiceProvider.GetRequiredService<IZeeqMessagePublisher>()
                .PublishAsync(message, deadline.Token);
            var route = (
                await scope
                    .ServiceProvider.GetRequiredService<ZeeqMessageRouteResolver>()
                    .ResolveRouteAsync(message, deadline.Token)
            ).RoutingKey;
            var name = new AwsSqsResourceNameValidator().QueueName(queues.Prefix, route);
            var queueUrl = (await queues.Client.GetQueueUrlAsync(name, deadline.Token)).QueueUrl;
            var received = await queues.Client.ReceiveMessageAsync(
                new ReceiveMessageRequest(queueUrl) { WaitTimeSeconds = 1 },
                deadline.Token
            );
            await Assert.That(received.Messages).HasSingleItem();
            await Assert.That(received.Messages[0].Body).Contains("producer-role-body");
            await queues.Client.DeleteMessageAsync(
                queueUrl,
                received.Messages[0].ReceiptHandle,
                deadline.Token
            );
        }
        foreach (var publication in topology.Publications)
        {
            var url = (
                await queues.Client.GetQueueUrlAsync(publication.ChannelName!.Value, deadline.Token)
            ).QueueUrl;
            var attributes = await queues.Client.GetQueueAttributesAsync(
                new GetQueueAttributesRequest(
                    url,
                    ["VisibilityTimeout", "ReceiveMessageWaitTimeSeconds"]
                ),
                deadline.Token
            );
            await Assert
                .That(attributes.Attributes["VisibilityTimeout"])
                .IsEqualTo(((int)publication.QueueAttributes.LockTimeout.TotalSeconds).ToString());
            await Assert
                .That(attributes.Attributes["ReceiveMessageWaitTimeSeconds"])
                .IsEqualTo("1");
        }
    }

    [Test]
    public async Task Validate_ReconcilesMissingQueues_Idempotently_WithoutChangingExistingAttributes()
    {
        await using var queues = new SqsTestQueue(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var topology = new AwsSqsMessagingTopology(
            new MessagingCatalogScanner().Scan(typeof(SqsPipelinePrototype).Assembly),
            MessagingOptions(),
            fixture.Options(queues.Prefix)
        );
        foreach (var publication in topology.Publications)
        {
            queues.Track(publication.ChannelName!.Value);
        }
        var service = new AwsSqsTopologyService(
            queues.Client,
            NullLogger<AwsSqsTopologyService>.Instance
        );
        await service.EnsureTopologyAsync(
            topology,
            registerProducers: true,
            registerConsumers: true,
            deadline.Token
        );
        var name = topology.Publications[0].ChannelName!.Value;
        var url = (await queues.Client.GetQueueUrlAsync(name, deadline.Token)).QueueUrl;
        await queues.Client.SetQueueAttributesAsync(
            url,
            new Dictionary<string, string> { ["VisibilityTimeout"] = "120" },
            deadline.Token
        );
        await service.EnsureTopologyAsync(
            topology,
            registerProducers: true,
            registerConsumers: true,
            deadline.Token
        );
        var attributes = await queues.Client.GetQueueAttributesAsync(
            url,
            ["VisibilityTimeout"],
            deadline.Token
        );
        await Assert.That(attributes.Attributes["VisibilityTimeout"]).IsEqualTo("120");
    }

    [Test]
    public async Task Reconciliation_PropagatesCancellation()
    {
        await using var queues = new SqsTestQueue(fixture);
        var topology = new AwsSqsMessagingTopology(
            new MessagingCatalogScanner().Scan(typeof(SqsPipelinePrototype).Assembly),
            MessagingOptions(),
            fixture.Options(queues.Prefix)
        );
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = new AwsSqsTopologyService(
            queues.Client,
            NullLogger<AwsSqsTopologyService>.Instance
        );
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.EnsureTopologyAsync(
                topology,
                registerProducers: true,
                registerConsumers: true,
                cancelled.Token
            )
        );
    }

    [Test]
    public async Task SqsAndGcp_ExpandTheSameLogicalRoutesAndHandlerSettings()
    {
        var catalog = new MessagingCatalogScanner().Scan(typeof(SqsPipelinePrototype).Assembly);
        var messaging = MessagingOptions();
        var sqs = new AwsSqsMessagingTopology(catalog, messaging, fixture.Options("parity"));
        var gcp = new GcpPubSubConsumerRegistry(
            catalog,
            messaging,
            new GcpPubSubMessagingOptions { ProjectId = "parity" }
        ).CreateSubscriptions();
        var sqsSettings = sqs
            .Subscriptions.OrderBy(s => s.RoutingKey.Value)
            .Select(s =>
                (
                    s.RoutingKey.Value,
                    s.RequestType,
                    s.BufferSize,
                    s.NoOfPerformers,
                    s.EmptyChannelDelay
                )
            )
            .ToArray();
        var gcpSettings = gcp.OrderBy(s => s.RoutingKey.Value)
            .Select(s =>
                (
                    s.RoutingKey.Value,
                    s.RequestType,
                    s.BufferSize,
                    s.NoOfPerformers,
                    s.EmptyChannelDelay
                )
            )
            .ToArray();
        await Assert.That(sqsSettings.SequenceEqual(gcpSettings)).IsTrue();
    }

    [Test]
    public async Task MatchingConcurrentReconciliation_UsesTheSameQueues()
    {
        await using var queues = new SqsTestQueue(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var topology = new AwsSqsMessagingTopology(
            new MessagingCatalogScanner().Scan(typeof(SqsPipelinePrototype).Assembly),
            MessagingOptions(),
            fixture.Options(queues.Prefix)
        );
        foreach (var publication in topology.Publications)
        {
            queues.Track(publication.ChannelName!.Value);
        }
        var service = new AwsSqsTopologyService(
            queues.Client,
            NullLogger<AwsSqsTopologyService>.Instance
        );
        await Task.WhenAll(
            service.EnsureTopologyAsync(
                topology,
                registerProducers: true,
                registerConsumers: true,
                deadline.Token
            ),
            service.EnsureTopologyAsync(
                topology,
                registerProducers: true,
                registerConsumers: true,
                deadline.Token
            )
        );
        foreach (var publication in topology.Publications)
        {
            var url = (
                await queues.Client.GetQueueUrlAsync(publication.ChannelName!.Value, deadline.Token)
            ).QueueUrl;
            await Assert.That(url).IsNotNull();
        }
    }

    [Test]
    public async Task RepeatedRegistration_FailsBeforeChangingTheHost()
    {
        await using var queues = new SqsTestQueue(fixture);
        var options = fixture.Options(queues.Prefix);
        var messaging = MessagingOptions();
        var topology = new AwsSqsMessagingTopology(
            new MessagingCatalogScanner().Scan(typeof(SqsPipelinePrototype).Assembly),
            messaging,
            options
        );
        foreach (var publication in topology.Publications)
        {
            queues.Track(publication.ChannelName!.Value);
        }
        var services = new ServiceCollection();
        services.AddZeeqAwsSqsMessageProducers(
            messaging,
            options,
            fixture.Credentials,
            CancellationToken.None,
            typeof(SqsPipelinePrototype).Assembly
        );
        var count = services.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Task.FromResult(
                services.AddZeeqAwsSqsMessageConsumers(
                    messaging,
                    options,
                    fixture.Credentials,
                    CancellationToken.None,
                    typeof(SqsPipelinePrototype).Assembly
                )
            )
        );
        await Assert.That(services.Count).IsEqualTo(count);
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
}
