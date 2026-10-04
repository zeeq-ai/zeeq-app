using Amazon.SQS.Model;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.AWSSQS;

namespace Zeeq.Platform.Messaging.AwsSqs.Tests.Integration;

/// <summary>Proves the pinned Brighter transport against Floci, independently of the Zeeq runtime.</summary>
[Category("SqsEmulator")]
[ClassDataSource<FlociFixture>(Shared = SharedType.PerTestSession)]
public sealed class BrighterSqsExplorationTests(FlociFixture fixture)
{
    [Test]
    [Arguments(OnMissingChannel.Create)]
    [Arguments(OnMissingChannel.Validate)]
    [Arguments(OnMissingChannel.Assume)]
    public async Task ExistingQueue_RoundTrips_MetadataAndAcknowledgement(OnMissingChannel policy)
    {
        await using var test = new SqsTestQueue(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var route = $"{test.Prefix}.roundtrip";
        await using (
            var created = await new ChannelFactory(test.Connection).CreateAsyncChannelAsync(
                test.Subscription(route),
                ct
            )
        )
        {
            await Assert.That(created.RoutingKey.Value).IsEqualTo(RoutingKey.Empty.Value);
        }

        await using var channel = await new ChannelFactory(test.Connection).CreateAsyncChannelAsync(
            test.Subscription(route, policy),
            ct
        );
        using var registry = await new SqsProducerRegistryFactory(
            test.Connection,
            [test.Publication(route, policy: policy)]
        ).CreateAsync(ct);
        var sent = SqsTestQueue.Message(route);
        await registry.LookupAsyncBy(new RoutingKey(route)).SendAsync(sent, ct);
        var received = await channel.ReceiveAsync(TimeSpan.FromSeconds(1), ct);

        await Assert.That(received.Header.MessageId).IsEqualTo(sent.Header.MessageId);
        await Assert.That(received.Header.CorrelationId).IsEqualTo(sent.Header.CorrelationId);
        await Assert.That(received.Body.Value).IsEqualTo(sent.Body.Value);
        // Pinned Brighter writes the queue URL as Topic, then reads its physical queue name.
        await Assert.That(received.Header.Topic.Value).IsEqualTo(route.Replace('.', '_'));
        await Assert
            .That(channel.RoutingKey.Value)
            .IsEqualTo(policy == OnMissingChannel.Assume ? route : RoutingKey.Empty.Value);
        Console.WriteLine(
            $"Endpoint={fixture.Endpoint}, queueUrl={(await test.Client.GetQueueUrlAsync(route.Replace('.', '_'), ct)).QueueUrl}"
        );

        await channel.AcknowledgeAsync(received, ct);
        await Assert
            .That((await channel.ReceiveAsync(TimeSpan.FromSeconds(1), ct)).Header.MessageType)
            .IsEqualTo(MessageType.MT_NONE);
    }

    [Test]
    public async Task ValidateMissingQueue_Fails_AndAssumeDoesNotProvision()
    {
        await using var test = new SqsTestQueue(fixture);
        var route = $"{test.Prefix}.missing";
        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
        {
            await using var channel = await new ChannelFactory(
                test.Connection
            ).CreateAsyncChannelAsync(test.Subscription(route, OnMissingChannel.Validate));
        });
        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
        {
            using var registry = await new SqsProducerRegistryFactory(
                test.Connection,
                [test.Publication(route, policy: OnMissingChannel.Validate)]
            ).CreateAsync();
        });
        await using var assumed = await new ChannelFactory(test.Connection).CreateAsyncChannelAsync(
            test.Subscription(route, OnMissingChannel.Assume)
        );
        // Brighter 10.6's Assume consumer skips validation; its producer still resolves the queue.
        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
        {
            using var assumedProducers = await new SqsProducerRegistryFactory(
                test.Connection,
                [test.Publication(route, policy: OnMissingChannel.Assume)]
            ).CreateAsync();
        });
        await Assert.ThrowsAsync<QueueDoesNotExistException>(async () =>
            await test.Client.GetQueueUrlAsync(route.Replace('.', '_'))
        );
    }

    [Test]
    public async Task VisibilityAndDelayedRequeue_Redeliver_UnacknowledgedMessage()
    {
        await using var test = new SqsTestQueue(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var route = $"{test.Prefix}.visibility";
        await using var channel = await new ChannelFactory(test.Connection).CreateAsyncChannelAsync(
            test.Subscription(route),
            ct
        );
        using var registry = await new SqsProducerRegistryFactory(
            test.Connection,
            [test.Publication(route)]
        ).CreateAsync(ct);
        var sent = SqsTestQueue.Message(route);
        await registry.LookupAsyncBy(new RoutingKey(route)).SendAsync(sent, ct);
        var first = await channel.ReceiveAsync(TimeSpan.FromSeconds(1), ct);
        await Assert.That(first.Header.MessageId).IsEqualTo(sent.Header.MessageId);
        await Assert
            .That((await channel.ReceiveAsync(TimeSpan.Zero, ct)).Header.MessageType)
            .IsEqualTo(MessageType.MT_NONE);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var second = await channel.ReceiveAsync(TimeSpan.FromSeconds(1), ct);
        await Assert.That(second.Header.MessageId).IsEqualTo(first.Header.MessageId);
        await Assert.That(await channel.RequeueAsync(second, TimeSpan.FromSeconds(2), ct)).IsTrue();
        await Assert
            .That((await channel.ReceiveAsync(TimeSpan.Zero, ct)).Header.MessageType)
            .IsEqualTo(MessageType.MT_NONE);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        var third = await channel.ReceiveAsync(TimeSpan.FromSeconds(1), ct);
        await Assert.That(third.Header.MessageId).IsEqualTo(first.Header.MessageId);
        await channel.AcknowledgeAsync(third, ct);
    }

    [Test]
    public async Task LongPoll_UsesSeconds_AndWaitsOnEmptyQueue()
    {
        await using var test = new SqsTestQueue(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var route = $"{test.Prefix}.longpoll";
        var attributes = new SqsAttributes(
            lockTimeout: TimeSpan.FromSeconds(30),
            timeOut: TimeSpan.FromSeconds(20)
        );
        await using var channel = await new ChannelFactory(test.Connection).CreateAsyncChannelAsync(
            test.Subscription(route, attributes: attributes),
            deadline.Token
        );
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var received = await channel.ReceiveAsync(TimeSpan.FromSeconds(20), deadline.Token);
        await Assert.That(received.Header.MessageType).IsEqualTo(MessageType.MT_NONE);
        await Assert.That(timer.Elapsed.TotalSeconds).IsGreaterThanOrEqualTo(19);
        await Assert.That(timer.Elapsed.TotalSeconds).IsLessThan(28);
    }

    [Test]
    public async Task NativeRedrive_MovesUnacknowledgedMessage_ToTransportDeadLetterQueue()
    {
        await using var test = new SqsTestQueue(fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var route = $"{test.Prefix}.redrive";
        var dlq = test.Track($"{test.Prefix}_dlq");
        var attributes = new SqsAttributes(
            lockTimeout: TimeSpan.FromSeconds(1),
            timeOut: TimeSpan.Zero,
            redrivePolicy: new RedrivePolicy(new ChannelName(dlq), maxReceiveCount: 2)
        );
        await using var channel = await new ChannelFactory(test.Connection).CreateAsyncChannelAsync(
            test.Subscription(route, attributes: attributes),
            ct
        );
        using var registry = await new SqsProducerRegistryFactory(
            test.Connection,
            [test.Publication(route, attributes)]
        ).CreateAsync(ct);
        var message = SqsTestQueue.Message(route);
        await registry.LookupAsyncBy(new RoutingKey(route)).SendAsync(message, ct);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await channel.ReceiveAsync(TimeSpan.Zero, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        var url = (await test.Client.GetQueueUrlAsync(dlq, ct)).QueueUrl;
        var deadLetters = await test.Client.ReceiveMessageAsync(
            new ReceiveMessageRequest(url) { WaitTimeSeconds = 1 },
            ct
        );
        await Assert.That(deadLetters.Messages).HasSingleItem();
        await Assert.That(deadLetters.Messages[0].Body).IsEqualTo(message.Body.Value);
    }
}
