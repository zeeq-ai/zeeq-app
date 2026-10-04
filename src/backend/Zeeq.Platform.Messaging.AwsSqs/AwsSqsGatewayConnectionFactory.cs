using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Paramore.Brighter.MessagingGateway.AWSSQS;

namespace Zeeq.Platform.Messaging.AwsSqs;

/// <summary>Builds Brighter connections without depending on the server or AWS deployment tooling.</summary>
public static class AwsSqsGatewayConnectionFactory
{
    /// <summary>Uses caller-supplied credentials, including task-role credentials or local dummy credentials.</summary>
    public static AWSMessagingGatewayConnection Create(
        AwsSqsMessagingOptions options,
        AWSCredentials credentials
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Region);
        if (
            options.ServiceUrl is not null
            && (
                !Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https")
            )
        )
        {
            throw new ArgumentException(
                "ServiceUrl must be an absolute HTTP endpoint.",
                nameof(options)
            );
        }

        return new AWSMessagingGatewayConnection(
            credentials,
            RegionEndpoint.GetBySystemName(options.Region),
            config =>
            {
                if (options.ServiceUrl is not null)
                {
                    config.ServiceURL = options.ServiceUrl;
                    config.AuthenticationRegion = options.Region;
                }
            }
        );
    }

    /// <summary>Creates a topology client with the same signing and endpoint settings as Brighter.</summary>
    public static AmazonSQSClient CreateClient(AWSMessagingGatewayConnection connection)
    {
        var config = new AmazonSQSConfig { RegionEndpoint = connection.Region };
        connection.ClientConfigAction?.Invoke(config);
        return new AmazonSQSClient(connection.Credentials, config);
    }
}
