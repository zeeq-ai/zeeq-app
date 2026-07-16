using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paramore.Brighter.ServiceActivator;

namespace Zeeq.Platform.Messaging;

/// <summary>
/// Starts and stops Brighter message consumers with the .NET host.
/// </summary>
/// <remarks>
/// Brighter's dependency-injection extension registers an <see cref="IDispatcher"/>
/// with the configured subscriptions, but the dispatcher does not consume until
/// <see cref="IDispatcher.Receive"/> is called. Transport adapters register this
/// hosted service only from their consumer setup path, so producer-only
/// processes can publish without opening message pumps.
/// </remarks>
public sealed class BrighterMessagingConsumerHostedService(
    IDispatcher dispatcher,
    ILogger<BrighterMessagingConsumerHostedService> logger
) : IHostedService
{
    /// <summary>
    /// Opens all configured Brighter consumers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token from the host startup process.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        dispatcher.Receive();

        var consumerCount = dispatcher.Consumers.Count();

        logger.LogInformation(
            "Started Brighter message queue consumers. Count: {ConsumerCount}",
            consumerCount
        );

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops all configured Brighter consumers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token from the host shutdown process.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await dispatcher.End();

        logger.LogInformation("Stopped Brighter message queue consumers.");
    }
}
