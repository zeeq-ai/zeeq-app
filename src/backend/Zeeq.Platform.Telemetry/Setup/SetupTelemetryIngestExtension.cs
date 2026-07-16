using Zeeq.Core.Common;
using Zeeq.Core.Models;
using Zeeq.Platform.Telemetry.Adapters;
using Zeeq.Platform.Telemetry.Adapters.ZeeqAgent;
using Zeeq.Platform.Telemetry.Adapters.ClaudeCode;
using Zeeq.Platform.Telemetry.Adapters.Codex;
using Zeeq.Platform.Telemetry.Adapters.Copilot;
using Zeeq.Platform.Telemetry.Filtering;
using Zeeq.Platform.Telemetry.Ingest.Import;
using Zeeq.Platform.Telemetry.Ingest.Otlp;
using Zeeq.Platform.Telemetry.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.Telemetry.Setup;

/// <summary>
/// Registers telemetry ingest services, adapters, and the processing background service.
/// </summary>
/// <remarks>
/// Endpoint types (<c>IEndpoint</c> / <c>IEndpointHandler</c>) are auto-registered
/// by <c>AddZeeqEndpoints()</c>'s classpath scan — no manual registration needed.
/// </remarks>
public static class SetupTelemetryIngestExtension
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers telemetry ingest services, filters, adapters, and the
        /// processing background service.
        /// </summary>
        /// <param name="settings">Telemetry configuration from <c>AppSettings</c>.</param>
        public IServiceCollection AddTelemetryIngest(TelemetrySettings settings)
        {
            // Settings
            services.AddSingleton(settings);

            // Ingest
            services.AddScoped<OtlpLogIngestService>();
            services.AddScoped<AgentTelemetryImportValidator>();
            services.AddScoped<AgentTelemetryImportOtlpMapper>();

            // Filters & extractors
            services.AddScoped<AgentTelemetryLogFilter>();
            services.AddScoped<AgentTelemetrySpanFilter>();
            services.AddScoped<TelemetryRawLogMetadataExtractor>();

            // Adapters — registered as concrete types plus all interfaces they
            // implement (IAgentTelemetryAdapter for processing, ITelemetryLogFilter
            // / ITelemetrySpanFilter for the defensive filter chain).
            services.AddScoped<ClaudeCodeTelemetryAdapter>();
            services.AddScoped<ITelemetryLogFilter>(sp =>
                sp.GetRequiredService<ClaudeCodeTelemetryAdapter>()
            );
            services.AddScoped<IAgentTelemetryAdapter>(sp =>
                sp.GetRequiredService<ClaudeCodeTelemetryAdapter>()
            );

            services.AddScoped<CodexTelemetryAdapter>();
            services.AddScoped<ITelemetryLogFilter>(sp =>
                sp.GetRequiredService<CodexTelemetryAdapter>()
            );
            services.AddScoped<IAgentTelemetryAdapter>(sp =>
                sp.GetRequiredService<CodexTelemetryAdapter>()
            );

            services.AddScoped<ZeeqAgentTelemetryAdapter>();
            services.AddScoped<ITelemetryLogFilter>(sp =>
                sp.GetRequiredService<ZeeqAgentTelemetryAdapter>()
            );
            services.AddScoped<IAgentTelemetryAdapter>(sp =>
                sp.GetRequiredService<ZeeqAgentTelemetryAdapter>()
            );

            services.AddScoped<CopilotChatTelemetryAdapter>();
            services.AddScoped<ITelemetrySpanFilter>(sp =>
                sp.GetRequiredService<CopilotChatTelemetryAdapter>()
            );
            services.AddScoped<IAgentTelemetryAdapter>(sp =>
                sp.GetRequiredService<CopilotChatTelemetryAdapter>()
            );

            // Processing
            services.AddSingleton<IAgentTelemetryCostEnricher, AgentTelemetryCostEnricher>();
            services.AddScoped<
                IAgentTelemetryPullRequestLinker,
                TelemetryPullRequestLinkingService
            >();
            services.AddHostedService<TelemetryProcessingService>();

            return services;
        }
    }
}
