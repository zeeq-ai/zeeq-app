using Zeeq.Core.Documents;
using Microsoft.Extensions.Logging;

namespace Zeeq.Platform.Ingest.Diagnostics;

/// <summary>
/// Builds a <see cref="RepositoryIngestRunner"/> wired for a dry run — every
/// document/run-record write is only logged, nothing is persisted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Intended usage: the <c>zeeq-dotnet-repl</c> skill, not production DI.</b>
/// Connect to the running <c>zeeq-server</c> process, resolve a scope's real
/// <see cref="IDocsPublicDocumentStore"/>/<see cref="ILibraryDocumentStore"/>
/// (so move-detection reads see real data), and the real
/// <c>IIngestWorkspaceProvider</c> (so the git clone is real, landing in
/// <c>/tmp</c> as usual), then run the dry-run runner against a real
/// <c>RepositoryIngestJob</c>. Nothing this produces is ever written back to
/// Postgres.
/// </para>
/// <example>
/// <code>
/// await services.UseAsync&lt;IIngestWorkspaceProvider&gt;(async workspaceProvider =&gt;
///     await services.UseAsync&lt;IDocsPublicDocumentStore&gt;(async publicStore =&gt;
///         await services.UseAsync&lt;ILibraryDocumentStore&gt;(async libraryStore =&gt;
///         {
///             var job = new RepositoryIngestJob { /* ... */ };
///             await using var workspace = await workspaceProvider.AcquireAsync(job, CancellationToken.None);
///             var runner = DryRunIngestRunnerFactory.Create(publicStore, libraryStore, Get&lt;ILoggerFactory&gt;());
///             var run = await runner.RunAsync(job, workspace, CancellationToken.None);
///             return run.Status;
///         }
///     )
/// );
/// </code>
/// </example>
/// </remarks>
public static class DryRunIngestRunnerFactory
{
    /// <summary>Builds a dry-run <see cref="RepositoryIngestRunner"/>.</summary>
    /// <param name="publicDocumentStore">
    /// The real, DI-resolved public document store — reads flow through to it;
    /// writes are only logged.
    /// </param>
    /// <param name="libraryDocumentStore">
    /// The real, DI-resolved library document store — reads flow through to
    /// it; writes are only logged.
    /// </param>
    /// <param name="loggerFactory">Used to create each component's logger.</param>
    public static RepositoryIngestRunner Create(
        IDocsPublicDocumentStore publicDocumentStore,
        ILibraryDocumentStore libraryDocumentStore,
        ILoggerFactory loggerFactory
    ) =>
        new(
            new DryRunDocsPublicDocumentStore(
                publicDocumentStore,
                loggerFactory.CreateLogger<DryRunDocsPublicDocumentStore>()
            ),
            new DryRunLibraryDocumentStore(
                libraryDocumentStore,
                loggerFactory.CreateLogger<DryRunLibraryDocumentStore>()
            ),
            new DryRunDocsIngestRunStore(loggerFactory.CreateLogger<DryRunDocsIngestRunStore>()),
            loggerFactory.CreateLogger<RepositoryIngestRunner>()
        );
}
