using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Zeeq.Core.Common;

namespace Zeeq.Runtime.Server.Setup;

/// <summary>
/// Logging setup for Serilog.
/// </summary>
internal static class SetupLoggingExtension
{
    // Keep the web and worker host overloads thin so both runtime modes share the
    // same Serilog sinks, filters, and DI registrations.
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Setup logging for Serilog with OpenTelemetry and console output.
        /// </summary>
        public void AddZeeqLogging()
        {
            Log.Logger = CreateLogger(builder.Environment);

            ConfigureLogging(builder.Logging, builder.Services);

            builder.Host.UseSerilog();
        }
    }

    extension(HostApplicationBuilder builder)
    {
        /// <summary>
        /// Setup logging for Serilog with OpenTelemetry and console output.
        /// </summary>
        public void AddZeeqLogging()
        {
            Log.Logger = CreateLogger(builder.Environment);
            ConfigureLogging(builder.Logging, builder.Services);
        }
    }

    private static Serilog.ILogger CreateLogger(IHostEnvironment environment)
    {
        var logConfiguration = new LoggerConfiguration();

        if (environment.IsDevelopment())
        {
            Console.WriteLine(
                $"Using development logging template: {LoggingConstants.DevelopmentTemplate}"
            );

            logConfiguration
                .WriteTo.Console(outputTemplate: LoggingConstants.DevelopmentTemplate)
                .WriteTo.OpenTelemetry(options =>
                {
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = "zeeq",
                        ["service.version"] = GitVersionInfo.TelemetryVersion,
                    };
                })
                .MinimumLevel.Debug();
        }
        else
        {
            logConfiguration
                .Enrich.WithProperty("GitSha", GitVersionInfo.ShortSha)
                .Enrich.WithProperty("Version", GitVersionInfo.DisplayVersion)
                //.WriteTo.Console(new CompactJsonFormatter())
                .WriteTo.Console(outputTemplate: LoggingConstants.ProductionTemplate)
                .WriteTo.OpenTelemetry(options =>
                {
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = "zeeq",
                        ["service.version"] = GitVersionInfo.TelemetryVersion,
                    };
                })
                .MinimumLevel.Debug();
        }

        logConfiguration
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("OpenIddict", LogEventLevel.Warning)
            .MinimumLevel.Override("Paramore.Brighter", LogEventLevel.Warning);
        // Comment out the OpenIddict override to see auth errors during local debugging.

        return logConfiguration.CreateLogger();
    }

    private static void ConfigureLogging(ILoggingBuilder logging, IServiceCollection services)
    {
        logging.ClearProviders();
        services.AddSerilog(Log.Logger);
        services.AddSingleton(Log.Logger);
    }
}

/// <summary>
/// Static class for wrapping logging constants
/// </summary>
public static class LoggingConstants
{
    /// <summary>
    /// The expression statement for development logging.
    /// </summary>
    /// <returns>An expression statement used for logging in development environments.</returns>
    public static readonly string DevelopmentTemplate =
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj} ({Here}){NewLine}{Exception}";

    /// <summary>
    /// The expression statement for production logging.
    /// </summary>
    /// <returns>An expression statement used for logging in production environments.</returns>
    public static readonly string ProductionTemplate =
        "[{Level:u3} {Version} {GitSha}] {Message:lj} ({Here}){NewLine}{Exception}";
}
