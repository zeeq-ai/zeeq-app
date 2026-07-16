using Zeeq.Core.Common;
using Zeeq.Core.Documents.Snippets;
using Microsoft.Extensions.DependencyInjection;

namespace Zeeq.Platform.Documents;

/// <summary>
/// Registers document-platform services: the snippet indexing settings slice, the query-time
/// search service, and the sweep hosted service.
/// </summary>
/// <remarks>
/// Split into two methods on purpose. <see cref="AddZeeqDocuments"/> registers inert
/// dependencies (the settings slice, <see cref="SnippetSearchService"/>) that are safe on every
/// process — the search service only runs on demand, per request, unlike the sweep.
/// <see cref="AddZeeqSnippetIndexing"/> registers the live
/// <see cref="SnippetIndexingHostedService"/> <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>,
/// which starts sweeping the moment it is registered — so it is called worker-only (plus
/// Development, so the single-process local Aspire topology runs the sweep), mirroring
/// <c>AddZeeqIngestScheduler</c>'s precedent.
/// </remarks>
public static class SetupZeeqDocuments
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the snippet indexing settings slice and the query-time search service. Safe
        /// on any process; does not start background work.
        /// </summary>
        /// <param name="appSettings">The runtime application settings.</param>
        public IServiceCollection AddZeeqDocuments(AppSettings appSettings)
        {
            services.AddSingleton(appSettings.SnippetIndexing);

            // Registered here (not left to AddZeeqLlm alone) so SnippetIndexingHostedService and
            // SnippetSearchService can depend on LlmEmbeddingSettings directly regardless of
            // Llm/Documents setup ordering in Program.cs.
            services.AddSingleton(appSettings.Llm.Embeddings);

            // Scoped: depends on the scoped ISnippetStore<T>/ILibraryDocumentStore stores.
            services.AddScoped<SnippetSearchService>();

            return services;
        }

        /// <summary>
        /// Registers the snippet indexing sweep hosted service — a live background service. Call
        /// once per deployment topology (worker host, plus Development web), not from every process.
        /// </summary>
        public IServiceCollection AddZeeqSnippetIndexing()
        {
            services.AddHostedService<SnippetIndexingHostedService>();

            return services;
        }
    }
}
