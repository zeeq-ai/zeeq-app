using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Zeeq.Core.Common;
using Zeeq.Core.Documents;

namespace Zeeq.Integrations.Notion;

/// <summary>
/// Registers Notion integration services.
/// </summary>
public static class SetupNotionIntegration
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(SetupNotionIntegration));

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the Notion SDK client factory and its resilience pipeline.
        /// </summary>
        /// <remarks>
        /// Unlike <c>AddZeeqGitHubIntegration</c>, this takes no app-level settings: v1's Access
        /// Token auth model (spec §3.1) has no app-wide client id/secret or webhook secret — every
        /// credential is a per-library pasted token resolved from <c>EncryptedValue</c> at call
        /// time, and the webhook signing key is likewise per-library (spec §3.4/§3.6). Webhook
        /// ingress and the sync runner that depend on those are later phases, not this one.
        /// </remarks>
        public IServiceCollection AddZeeqNotionIntegration()
        {
            Log.Here().Information("⚙️  Adding Notion integration");

            services.AddFluentlyHttpClient();
            services.AddNotionResilience();
            services.AddSingleton<IZeeqNotionClientFactory, ZeeqNotionClientFactory>();
            services.AddSingleton<INotionTokenValidator, NotionTokenValidator>();
            services.AddSingleton<NotionCallbackTokenProtector>();
            services.AddSingleton<NotionWebhookSignatureVerifier>();

            return services;
        }
    }
}
