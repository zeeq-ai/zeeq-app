using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Creates missing queues without deleting or mutating existing topology, as in the GCP adapter.</summary>
public sealed partial class AwsSqsTopologyService(
    IAmazonSQS client,
    ILogger<AwsSqsTopologyService> logger
)
{
    /// <summary>Reconciles the selected process role's queues before Brighter validates its channels.</summary>
    public async Task EnsureTopologyAsync(
        AwsSqsMessagingTopology topology,
        bool registerProducers,
        bool registerConsumers,
        CancellationToken cancellationToken
    )
    {
        var desired = topology.Publications.Where(publication =>
            registerProducers
            || (
                registerConsumers
                && topology.Subscriptions.Any(subscription =>
                    subscription.ChannelName == publication.ChannelName
                )
            )
        );
        var createdQueues = 0;
        await Parallel.ForEachAsync(
            desired,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 8,
            },
            async (publication, token) =>
            {
                var name = publication.ChannelName!.Value;
                try
                {
                    await client.GetQueueUrlAsync(name, token);
                }
                catch (QueueDoesNotExistException)
                {
                    var attributes = publication.QueueAttributes;
                    // CreateQueue is idempotent for matching attributes, including simultaneous startup.
                    // NOTE: QueueNameExists means conflicting attributes, not a matching startup race.
                    // Preserve that failure rather than masking configuration disagreements.
                    // https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_CreateQueue.html
                    await client.CreateQueueAsync(
                        new CreateQueueRequest
                        {
                            QueueName = name,
                            Attributes = new Dictionary<string, string>
                            {
                                ["VisibilityTimeout"] = Seconds(attributes.LockTimeout),
                                ["ReceiveMessageWaitTimeSeconds"] = Seconds(
                                    attributes.TimeOut ?? TimeSpan.Zero
                                ),
                                ["MessageRetentionPeriod"] = Seconds(
                                    attributes.MessageRetentionPeriod
                                ),
                                ["DelaySeconds"] = Seconds(attributes.DelaySeconds),
                            },
                            Tags = attributes.Tags,
                        },
                        token
                    );
                    Interlocked.Exchange(ref createdQueues, 1);
                    LogQueueCreated(name);
                }
            }
        );
        if (createdQueues != 0)
        {
            // AWS requires one second after CreateQueue before using the new queues.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static string Seconds(TimeSpan value) =>
        ((int)value.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created AWS SQS queue {QueueName}.")]
    private partial void LogQueueCreated(string queueName);
}
