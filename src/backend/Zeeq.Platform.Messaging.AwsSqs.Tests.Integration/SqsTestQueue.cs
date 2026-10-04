using Amazon.SQS;
using Amazon.SQS.Model;
using Paramore.Brighter;
using Paramore.Brighter.MessagingGateway.AWSSQS;

namespace Zeeq.Platform.Messaging.AwsSqs.Tests.Integration;

/// <summary>Owns only uniquely named queues created by one test.</summary>
internal sealed class SqsTestQueue(FlociFixture fixture) : IAsyncDisposable
{
    private readonly HashSet<string> _queueNames = [];
    public string Prefix { get; } = $"zsq-{Guid.NewGuid():N}"[..14];
    public AmazonSQSClient Client { get; } = fixture.Client();
    public AWSMessagingGatewayConnection Connection =>
        AwsSqsGatewayConnectionFactory.Create(fixture.Options(Prefix), fixture.Credentials);

    public string Track(string queueName)
    {
        _queueNames.Add(queueName.Replace('.', '_'));
        return queueName;
    }

    public SqsPublication Publication(
        string route,
        SqsAttributes? attributes = null,
        OnMissingChannel policy = OnMissingChannel.Create
    ) =>
        new()
        {
            Topic = new RoutingKey(route),
            ChannelName = new ChannelName(Track(route)),
            MakeChannels = policy,
            QueueAttributes =
                attributes
                ?? new SqsAttributes(
                    lockTimeout: TimeSpan.FromSeconds(2),
                    timeOut: TimeSpan.FromSeconds(1)
                ),
        };

    public SqsSubscription Subscription(
        string route,
        OnMissingChannel policy = OnMissingChannel.Create,
        SqsAttributes? attributes = null
    ) =>
        new(
            subscriptionName: new SubscriptionName(route),
            channelName: new ChannelName(Track(route)),
            channelType: ChannelType.PointToPoint,
            routingKey: new RoutingKey(route),
            requestType: typeof(Event),
            bufferSize: 1,
            timeOut: TimeSpan.FromSeconds(1),
            messagePumpType: MessagePumpType.Proactor,
            queueAttributes: attributes
                ?? new SqsAttributes(
                    lockTimeout: TimeSpan.FromSeconds(2),
                    timeOut: TimeSpan.FromSeconds(1)
                ),
            makeChannels: policy
        );

    public static Paramore.Brighter.Message Message(string route) =>
        new(
            new MessageHeader(
                Id.Random(),
                new RoutingKey(route),
                MessageType.MT_EVENT,
                correlationId: Id.Random()
            ),
            new MessageBody("transport-proof")
        );

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            foreach (var name in _queueNames)
            {
                try
                {
                    var url = (await Client.GetQueueUrlAsync(name, cleanup.Token)).QueueUrl;
                    await Client.DeleteQueueAsync(url, cleanup.Token);
                }
                catch (QueueDoesNotExistException)
                {
                    // Validate/Assume tests intentionally leave some tracked queues absent.
                }
            }
        }
        finally
        {
            Client.Dispose();
        }
    }
}
