using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Zeeq.Platform.Metrics;

/// <summary>
/// Registers the metrics capture pipeline.
/// </summary>
/// <remarks>
/// <see cref="AddZeeqMetrics" /> registers the live
/// <see cref="MetricsIngestionHostedService" /> — a <see cref="BackgroundService" />
/// that starts its <c>MeterListener</c> immediately. Call it once per producer
/// process (both the web host and the worker host emit Zeeq metrics). The batch
/// consumer (<see cref="MetricBatchMessageHandler" />) and the write store are wired
/// separately: the handler is discovered through messaging-catalog scanning, and
/// <c>IMetricEventStore</c> is registered by the Postgres data layer.
/// </remarks>
public static class SetupZeeqMetrics
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the metrics ingestion hosted service (MeterListener capture + flush).
        /// </summary>
        public IServiceCollection AddZeeqMetrics()
        {
            services.AddHostedService<MetricsIngestionHostedService>();

            return services;
        }
    }
}
