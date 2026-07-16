using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeeq.Runtime.Server;

/// <summary>
/// Logs process-wide exceptions that escape normal worker message handling.
/// </summary>
/// <remarks>
/// Generic Host and Brighter both log many expected failures, but process-level
/// exception hooks catch the gaps: unhandled AppDomain exceptions and
/// unobserved task exceptions. This service is registered only by
/// <see cref="ZeeqWorkerHost"/>, so HTTP runtime behavior stays unchanged.
/// </remarks>
internal sealed class ZeeqWorkerGlobalExceptionLogger(
    ILogger<ZeeqWorkerGlobalExceptionLogger> logger
) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        return Task.CompletedTask;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            logger.LogCritical(
                exception,
                "Unhandled exception escaped the Zeeq worker process. IsTerminating={IsTerminating}",
                args.IsTerminating
            );

            return;
        }

        logger.LogCritical(
            "Unhandled non-Exception object escaped the Zeeq worker process. IsTerminating={IsTerminating}; ExceptionObject={ExceptionObject}",
            args.IsTerminating,
            args.ExceptionObject
        );
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        logger.LogCritical(
            args.Exception,
            "Unobserved task exception surfaced in the Zeeq worker process."
        );
    }
}
