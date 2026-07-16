using Zeeq.Core.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeeq.Runtime.Server;

/// <summary>
/// Emits a simple periodic heartbeat from the Cloud Run worker process.
/// </summary>
/// <remarks>
/// Worker pools have no HTTP ingress, so this hosted service provides a
/// low-cost liveness signal in Cloud Run logs that is independent of message
/// traffic. It is registered only by <see cref="ZeeqWorkerHost"/> and is not
/// part of the web runtime.
/// </remarks>
internal sealed class ZeeqWorkerHeartbeatService(ILogger<ZeeqWorkerHeartbeatService> logger)
    : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickCount = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatInterval, stoppingToken);

            tickCount++;

            logger.LogInformation(
                "💓 Zeeq worker heartbeat tick {TickCount} ({Sha} built {BuildTimeUtc} UTC / {BuildTimeEst}). Worker process is running.",
                tickCount,
                GitVersionInfo.Sha ?? "unknown",
                GitVersionInfo.BuildTimeUtc?.ToString("o"),
                GitVersionInfo.BuildTimeEst ?? "unknown"
            );
        }
    }
}
