using Zeeq.Core.Common;
using Zeeq.Core.Documents.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.Ingest;

/// <summary>
/// Registers repository ingest services that are independent of runtime
/// dispatch (token resolution, the runner core).
/// </summary>
/// <remarks>
/// Store implementations come from the active data provider (already
/// registered via <c>AddPostgres</c>) and the GitHub App token client comes
/// from <c>AddZeeqGitHubIntegration</c> — both are prerequisites callers must
/// register before this. Dispatcher and workspace-provider registration lives
/// in the runtime-specific project (<c>Zeeq.Platform.Dispatch.Process</c>'s
/// <c>AddZeeqDispatchProcess</c>), not here, so this package stays runtime-agnostic.
/// </remarks>
public static class SetupZeeqIngest
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the ingest token provider, runner, and manual-trigger endpoint dependencies.
        /// </summary>
        /// <remarks>
        /// <see cref="IngestGitHubTokenProvider"/> and (in
        /// <c>Zeeq.Platform.Dispatch.Process</c>) <c>LocalTempWorkspaceProvider</c> take
        /// the full <see cref="AppSettings"/> in their constructors and resolve it from DI
        /// directly — the composition root already registers it as a singleton (see
        /// <c>ZeeqWorkerHost</c>). The manual-trigger handlers only need the
        /// <see cref="IngestSettings"/> slice (rate-limit window/threshold), so that's
        /// registered explicitly here, matching how other feature setups register their
        /// settings slice. Endpoint and <c>IEndpointHandler</c> types
        /// (<see cref="IngestEndpoints"/>, <see cref="IngestAdminEndpoints"/>,
        /// <see cref="TriggerLibraryIngestHandler"/>,
        /// <see cref="TriggerPublicSourceIngestHandler"/>) are picked up by the runtime's
        /// <c>Scrutor</c> scan once this assembly is referenced — no explicit registration
        /// needed for them.
        /// </remarks>
        public IServiceCollection AddZeeqIngest(AppSettings appSettings)
        {
            services.AddSingleton(appSettings.Ingest);
            services.AddScoped<IIngestGitHubTokenProvider, IngestGitHubTokenProvider>();
            services.AddScoped<RepositoryIngestRunner>();

            return services;
        }

        /// <summary>
        /// Adds the periodic scheduler that claims due public sources and
        /// publishes sync requests.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from <see cref="AddZeeqIngest"/> — this
        /// registers a live <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
        /// that starts polling immediately, unlike the rest of that method's
        /// registrations, which are inert until something calls them. Call
        /// this once per deployment topology (worker host only), not from
        /// every process that also needs the ingest runner/token provider —
        /// see <see cref="IngestSchedulerHostedService"/>'s remarks for why.
        /// </remarks>
        public IServiceCollection AddZeeqIngestScheduler()
        {
            services.AddHostedService<IngestSchedulerHostedService>();

            return services;
        }

        /// <summary>
        /// Adds the periodic recovery sweep that clears stalled repository sync leases.
        /// </summary>
        public IServiceCollection AddZeeqStalledIngestSyncCleanup()
        {
            services.AddHostedService<IngestStalledSyncCleanupHostedService>();

            return services;
        }
    }
}
