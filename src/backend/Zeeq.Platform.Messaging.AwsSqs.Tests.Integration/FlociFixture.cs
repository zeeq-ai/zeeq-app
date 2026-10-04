using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Testcontainers.Floci;
using TUnit.Core.Interfaces;

namespace Zeeq.Platform.Messaging.AwsSqs.Tests.Integration;

/// <summary>Uses an explicit local endpoint or an isolated, pinned Floci container.</summary>
public sealed class FlociFixture : IAsyncInitializer, IAsyncDisposable
{
    private FlociContainer? _container;
    public string Endpoint { get; private set; } = "";
    public BasicAWSCredentials Credentials { get; } = new("test", "test");

    public async Task InitializeAsync()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("ZEEQ_SQS_TEST_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            var uri = new Uri(configuredEndpoint);
            if (!uri.IsLoopback)
            {
                throw new InvalidOperationException(
                    "ZEEQ_SQS_TEST_ENDPOINT must be a loopback emulator endpoint."
                );
            }
            Endpoint = configuredEndpoint;
            return;
        }

        _container = new FlociBuilder("floci/floci:2.1.0").Build();
        await _container.StartAsync();
        Endpoint = _container.GetConnectionString();
        Console.WriteLine($"Isolated Floci endpoint: {Endpoint}");
    }

    public AwsSqsMessagingOptions Options(string prefix, int longPollSeconds = 1) =>
        new()
        {
            ServiceUrl = Endpoint,
            QueuePrefix = prefix,
            LongPollSeconds = longPollSeconds,
        };

    public AmazonSQSClient Client() =>
        new(
            Credentials,
            new AmazonSQSConfig
            {
                ServiceURL = Endpoint,
                AuthenticationRegion = RegionEndpoint.USEast1.SystemName,
            }
        );

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
