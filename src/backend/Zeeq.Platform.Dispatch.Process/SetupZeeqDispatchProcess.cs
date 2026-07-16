using Zeeq.Core.Documents.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.Dispatch.Process;

/// <summary>
/// Registers the in-process ingest dispatch runtime: git subprocess handling,
/// local-temp workspace acquisition, and the dispatcher itself.
/// </summary>
/// <remarks>
/// Requires <c>Zeeq.Platform.Ingest</c>'s <c>AddZeeqIngest()</c> (the
/// runner and token provider) and the active data provider's
/// <c>IDocsIngestRunStore</c> registration to already be present — this setup
/// only adds the runtime-specific pieces. Registers
/// <see cref="IRepositoryIngestDispatcher"/> directly rather than through a
/// keyed multi-runtime resolver: with exactly one dispatcher implementation in
/// v1, a resolver (spec §4.1's "keyed resolver picks the registered dispatcher
/// whose Runtime matches") has nothing to select between yet. Add the resolver
/// when a second <see cref="IRepositoryIngestDispatcher"/> (isolated process,
/// Cloud Run Job) actually exists — building it against one implementation
/// would just be unverifiable speculation about the resolution API.
/// </remarks>
public static class SetupZeeqDispatchProcess
{
    extension(IServiceCollection services)
    {
        /// <summary>Adds the in-process ingest dispatch runtime.</summary>
        public IServiceCollection AddZeeqDispatchProcess()
        {
            services.AddSingleton<GitCommandRunner>();
            services.AddScoped<IIngestWorkspaceProvider, LocalTempWorkspaceProvider>();
            services.AddScoped<IRepositoryIngestDispatcher, InProcessIngestDispatcher>();

            return services;
        }
    }
}
